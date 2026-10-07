using System.Diagnostics;
using Domestique.Core;
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
    // ▸ Phase 3 und 8: weitere Felder hier

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

        state.Update(s => s with { Connected = true });
        // ▸ Phase 3: Steuerung hier
    }

    private async Task<GattCharacteristic?> GetCharacteristicAsync(Guid uuid)
    {
        var r = await _ftms!.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached);
        return r.Status == GattCommunicationStatus.Success && r.Characteristics.Count > 0 ? r.Characteristics[0] : null;
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

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        bool connected = sender.ConnectionStatus == BluetoothConnectionStatus.Connected;
        state.Update(s => s with { Connected = connected });
        // ▸ Phase 8: Wiederverbinden hier
    }

    // ▸ Phase 3 und 8: weitere Methoden hier

    public ValueTask DisposeAsync()
    {
        if (_bikeData is not null) _bikeData.ValueChanged -= OnBikeData;
        if (_device is not null) _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        // ▸ Phase 3: Status-Abo lösen
        _ftms?.Dispose();
        _session?.Dispose();
        _device?.Dispose();
        state.Update(s => s with { Connected = false });            // Statusereignis ist schon abgemeldet
        return ValueTask.CompletedTask;
    }
}