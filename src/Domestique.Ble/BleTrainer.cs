using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Domestique.Core;
using Domestique.Core.Control;
using Domestique.Core.Protocol;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace Domestique.Ble;

public sealed class BleTrainer(SessionState state) : IAsyncDisposable
{
    private readonly PowerAverager _avg = new(TimeSpan.FromSeconds(3));
    private BluetoothLEDevice? _device;
    private GattSession? _session;
    private GattDeviceService? _ftms;
    private GattCharacteristic? _bikeData;
    private GattCharacteristic? _controlPoint;
    private GattCharacteristic? _status;
    public ControlPoint? Control { get; private set; }               // null: Trainer nur lesbar
    public string Name => string.IsNullOrWhiteSpace(_device?.Name) ? "Trainer" : _device.Name;
    public (int Min, int Max)? PowerRange { get; private set; }
    private bool _wasDisconnected;

    public async Task ConnectAsync(ulong address)
    {
        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
            ?? throw new InvalidOperationException("Trainer nicht erreichbar.");
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;

        _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId);
        _session.MaintainConnection = true;                         // Windows hält die Verbindung

        var services = await _device.GetGattServicesForUuidAsync(BleUuids.FitnessMachineService, BluetoothCacheMode.Uncached);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            throw new InvalidOperationException($"FTMS-Service nicht gefunden ({services.Status}).");
        _ftms = services.Services[0];

        _bikeData = await GetCharacteristicAsync(BleUuids.IndoorBikeData)
            ?? throw new InvalidOperationException("Indoor Bike Data fehlt.");
        _bikeData.ValueChanged += OnBikeData;
        var result = await _bikeData.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (result != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"Abo fehlgeschlagen ({result}).");

        await ConnectControlAsync();
        state.Update(s => s with { Connected = true });
        RawLog.Note($"Trainer verbunden ({_device.Name}), ERG-Bereich {PowerRange?.ToString() ?? "unbekannt"}");
    }

    // Steuerung ist optional: ohne Control Point bleibt der Trainer lesbar.
    private async Task ConnectControlAsync()
    {
        PowerRange = await ReadPowerRangeAsync();

        _status = await GetCharacteristicAsync(BleUuids.MachineStatus);
        if (_status is not null)
        {
            _status.ValueChanged += OnStatus;
            await _status.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
        }

        _controlPoint = await GetCharacteristicAsync(BleUuids.ControlPoint);
        if (_controlPoint is null) { RawLog.Note("Kein Control Point, Trainer nur lesbar"); return; }
        _controlPoint.ValueChanged += OnControlPoint;
        var s = await _controlPoint.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Indicate);    // 1. erst abonnieren
        if (s != GattCommunicationStatus.Success) { RawLog.Note($"Control Point nicht abonnierbar ({s})"); return; }
        Control = new ControlPoint(WriteControlAsync);
        await Control.TakeControlAsync();                                     // 2. dann Kontrolle holen und starten
    }

    private async Task<GattCharacteristic?> GetCharacteristicAsync(Guid uuid)
    {
        var r = await _ftms!.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached);
        return r.Status == GattCommunicationStatus.Success && r.Characteristics.Count > 0 ? r.Characteristics[0] : null;
    }

    // Supported Power Range (0x2AD8): Minimum und Maximum für ERG, je sint16
    private async Task<(int Min, int Max)?> ReadPowerRangeAsync()
    {
        if (await GetCharacteristicAsync(BleUuids.SupportedPowerRange) is not { } c) return null;
        var r = await c.ReadValueAsync(BluetoothCacheMode.Uncached);
        if (r.Status != GattCommunicationStatus.Success) return null;
        var b = r.Value.ToBytes();
        RawLog.Write("2AD8", b);
        if (b.Length < 4) return null;
        int min = BinaryPrimitives.ReadInt16LittleEndian(b), max = BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(2));
        return max > min ? (Math.Max(0, min), max) : null;
    }

    private async Task<bool> WriteControlAsync(byte[] command, CancellationToken ct)
    {
        RawLog.Write("2AD9>", command);
        var r = await _controlPoint!.WriteValueWithResultAsync(command.AsBuffer(), GattWriteOption.WriteWithResponse).AsTask(ct);
        return r.Status == GattCommunicationStatus.Success;
    }

    private void OnBikeData(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var bytes = args.CharacteristicValue.ToBytes();
        RawLog.Write("2AD2", bytes);
        var d = IndoorBikeDataParser.Parse(bytes);
        if (d is null) { RawLog.Note("2AD2 nicht lesbar"); return; }

        long now = Stopwatch.GetTimestamp();
        int? avg = d.PowerW is int w ? _avg.Add(now, w) : null;
        state.Update(s => s with
        {
            PowerW = d.PowerW ?? s.PowerW,
            Power3sW = avg ?? s.Power3sW,
            CadenceRpm = d.CadenceRpm ?? s.CadenceRpm,
            HeartRateBpm = s.StrapConnected ? s.HeartRateBpm : d.HeartRateBpm ?? s.HeartRateBpm,
            TrainerSpeedKmh = d.SpeedKmh ?? s.TrainerSpeedKmh,
            LastPacketTimestamp = now,
        });
    }

    private void OnControlPoint(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var bytes = args.CharacteristicValue.ToBytes();
        RawLog.Write("2AD9", bytes);                                // 800501 = Set Target Power erfolgreich
        Control?.OnIndication(bytes);
    }

    private void OnStatus(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var bytes = args.CharacteristicValue.ToBytes();
        RawLog.Write("2ADA", bytes);
        if (Control is not null) _ = Control.OnMachineStatus(bytes);  // FF = Kontrolle verloren
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        bool connected = sender.ConnectionStatus == BluetoothConnectionStatus.Connected;
        state.Update(s => s with { Connected = connected });
        if (!connected)
        {
            _wasDisconnected = true;
            Control?.OnDisconnected();
            RawLog.Note("Trainer getrennt");
        }
        else if (_wasDisconnected)
        {
            _wasDisconnected = false;
            _ = ResubscribeAsync();
        }
    }

    // Review #4: Windows verbindet selbst neu (MaintainConnection), aber Abos und Kontrolle sind weg.
    private async Task ResubscribeAsync()
    {
        RawLog.Note("Trainer wieder da, abonniere neu");
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (await SubscribeAsync(_bikeData, GattClientCharacteristicConfigurationDescriptorValue.Notify)
                    && await SubscribeAsync(_status, GattClientCharacteristicConfigurationDescriptorValue.Notify)
                    && await SubscribeAsync(_controlPoint, GattClientCharacteristicConfigurationDescriptorValue.Indicate))
                {
                    if (Control is not null) await Control.TakeControlAsync();     // Kontrolle, Start, letzter Zielwert
                    return;
                }
            }
            catch (Exception ex) { RawLog.Note($"Neu-Abo Versuch {attempt}: {ex.Message}"); }
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        RawLog.Note("Neu-Abo nach Reconnect fehlgeschlagen");
    }

    private static async Task<bool> SubscribeAsync(GattCharacteristic? c, GattClientCharacteristicConfigurationDescriptorValue value) =>
        c is null || await c.WriteClientCharacteristicConfigurationDescriptorAsync(value) == GattCommunicationStatus.Success;

    public async ValueTask DisposeAsync()
    {
        if (Control is not null) await Control.DisposeAsync();
        if (_bikeData is not null) _bikeData.ValueChanged -= OnBikeData;
        if (_controlPoint is not null) _controlPoint.ValueChanged -= OnControlPoint;
        if (_status is not null) _status.ValueChanged -= OnStatus;
        if (_device is not null) _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        _ftms?.Dispose();
        _session?.Dispose();
        _device?.Dispose();
        state.Update(s => s with { Connected = false });            // Statusereignis ist schon abgemeldet
    }
}
