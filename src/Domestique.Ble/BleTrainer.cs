using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Domestique.Core;
using Domestique.Core.Control;
using Domestique.Core.Protocol;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace Domestique.Ble;

// FTMS smart trainer: reads Indoor Bike Data and, if available, controls it through the Control Point.
public sealed class BleTrainer(SessionState state) : IAsyncDisposable
{
    private readonly PowerAverager _avg = new(TimeSpan.FromSeconds(3));
    private BluetoothLEDevice? _device;
    private GattSession? _session;
    private GattDeviceService? _ftms;
    private GattCharacteristic? _bikeData;
    private GattCharacteristic? _controlPoint;
    private GattCharacteristic? _status;
    public ControlPoint? Control { get; private set; }               // null: trainer is read-only
    public string Name => string.IsNullOrWhiteSpace(_device?.Name) ? "Trainer" : _device.Name;
    public (int Min, int Max)? PowerRange { get; private set; }
    private bool _wasDisconnected;

    public async Task ConnectAsync(ulong address)
    {
        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
            ?? throw new InvalidOperationException("Trainer not reachable.");
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;

        _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId);
        _session.MaintainConnection = true;                         // Windows keeps the connection alive

        var services = await _device.GetGattServicesForUuidAsync(BleUuids.FitnessMachineService, BluetoothCacheMode.Uncached);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            throw new InvalidOperationException($"FTMS service not found ({services.Status}).");
        _ftms = services.Services[0];

        _bikeData = await GetCharacteristicAsync(BleUuids.IndoorBikeData)
            ?? throw new InvalidOperationException("Indoor Bike Data missing.");
        _bikeData.ValueChanged += OnBikeData;
        var result = await _bikeData.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (result != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"Subscription failed ({result}).");

        await ConnectControlAsync();
        state.Update(s => s with { Connected = true });
        RawLog.Note($"Trainer connected ({_device.Name}), ERG range {PowerRange?.ToString() ?? "unknown"}");
    }

    // Control is optional: without a Control Point the trainer stays readable.
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
        if (_controlPoint is null) { RawLog.Note("No Control Point, trainer is read-only"); return; }
        _controlPoint.ValueChanged += OnControlPoint;
        var s = await _controlPoint.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Indicate);    // subscribe first,
        if (s != GattCommunicationStatus.Success) { RawLog.Note($"Control Point can't be subscribed ({s})"); return; }
        Control = new ControlPoint(WriteControlAsync);
        await Control.TakeControlAsync();                                     // then request control and start
    }

    private async Task<GattCharacteristic?> GetCharacteristicAsync(Guid uuid)
    {
        var r = await _ftms!.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached);
        return r.Status == GattCommunicationStatus.Success && r.Characteristics.Count > 0 ? r.Characteristics[0] : null;
    }

    // Supported Power Range (0x2AD8): ERG minimum and maximum, sint16 each.
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
        if (d is null) { RawLog.Note("2AD2 not readable"); return; }

        long now = Stopwatch.GetTimestamp();
        int? avg = d.PowerW is int w ? _avg.Add(now, w) : null;
        state.Update(s => s with
        {
            PowerW = d.PowerW ?? s.PowerW,
            Power3sW = avg ?? s.Power3sW,
            CadenceRpm = d.CadenceRpm ?? s.CadenceRpm,
            HeartRateBpm = s.StrapConnected ? s.HeartRateBpm : d.HeartRateBpm ?? s.HeartRateBpm,
            LastPacketTimestamp = now,
        });
    }

    private void OnControlPoint(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var bytes = args.CharacteristicValue.ToBytes();
        RawLog.Write("2AD9", bytes);                                // 800501 = Set Target Power succeeded
        Control?.OnIndication(bytes);
    }

    private void OnStatus(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var bytes = args.CharacteristicValue.ToBytes();
        RawLog.Write("2ADA", bytes);
        if (Control is not null) _ = Control.OnMachineStatus(bytes);  // FF = control lost
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        bool connected = sender.ConnectionStatus == BluetoothConnectionStatus.Connected;
        state.Update(s => s with { Connected = connected });
        if (!connected)
        {
            _wasDisconnected = true;
            Control?.OnDisconnected();
            RawLog.Note("Trainer disconnected");
        }
        else if (_wasDisconnected)
        {
            _wasDisconnected = false;
            _ = ResubscribeAsync();
        }
    }

    // Windows reconnects by itself (MaintainConnection), but subscriptions and control are gone.
    private async Task ResubscribeAsync()
    {
        RawLog.Note("Trainer back, resubscribing");
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (await SubscribeAsync(_bikeData, GattClientCharacteristicConfigurationDescriptorValue.Notify)
                    && await SubscribeAsync(_status, GattClientCharacteristicConfigurationDescriptorValue.Notify)
                    && await SubscribeAsync(_controlPoint, GattClientCharacteristicConfigurationDescriptorValue.Indicate))
                {
                    if (Control is not null) await Control.TakeControlAsync();     // control, start, last target
                    return;
                }
            }
            catch (Exception ex) { RawLog.Note($"Resubscribe attempt {attempt}: {ex.Message}"); }
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        RawLog.Note("Resubscribe after reconnect failed");
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
        state.Update(s => s with { Connected = false });            // the status event is already unsubscribed
    }
}
