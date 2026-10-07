using Domestique.Core;
using Domestique.Core.Protocol;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace Domestique.Ble;

// Brustgurt über den Heart Rate Service. Gleiches Muster wie beim Trainer, inklusive Neu-Abo nach einem Abbruch.
public sealed class BleHeartRate(SessionState state) : IAsyncDisposable
{
    private BluetoothLEDevice? _device;
    private GattSession? _session;
    private GattDeviceService? _service;
    private GattCharacteristic? _measurement;
    private bool _wasDisconnected;

    public async Task ConnectAsync(ulong address)
    {
        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
            ?? throw new InvalidOperationException("Pulsgurt nicht erreichbar.");
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;
        _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId);
        _session.MaintainConnection = true;

        var services = await _device.GetGattServicesForUuidAsync(BleUuids.HeartRateService, BluetoothCacheMode.Uncached);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            throw new InvalidOperationException($"Heart-Rate-Service fehlt ({services.Status}).");
        _service = services.Services[0];

        var chars = await _service.GetCharacteristicsForUuidAsync(BleUuids.HeartRateMeasurement, BluetoothCacheMode.Uncached);
        if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0)
            throw new InvalidOperationException("Pulsmessung fehlt.");
        _measurement = chars.Characteristics[0];
        _measurement.ValueChanged += OnValue;
        var s = await SubscribeAsync();
        if (s != GattCommunicationStatus.Success) throw new InvalidOperationException($"Puls-Abo fehlgeschlagen ({s}).");
        RawLog.Note("Pulsgurt verbunden");
    }

    private async Task<GattCommunicationStatus> SubscribeAsync() =>
        await _measurement!.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);

    private void OnValue(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var bytes = args.CharacteristicValue.ToBytes();
        RawLog.Write("2A37", bytes);
        if (HeartRateParser.Parse(bytes) is int bpm)
            state.Update(s => s with { HeartRateBpm = bpm, StrapConnected = true });
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            _wasDisconnected = true;
            RawLog.Note("Pulsgurt getrennt");
            state.Update(s => s with { StrapConnected = false, HeartRateBpm = null });   // kein alter Puls in der Anzeige
        }
        else if (_wasDisconnected)
        {
            _wasDisconnected = false;
            _ = ResubscribeAsync();
        }
    }

    private async Task ResubscribeAsync()
    {
        RawLog.Note("Pulsgurt wieder da, abonniere neu");
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try { if (await SubscribeAsync() == GattCommunicationStatus.Success) return; }
            catch (Exception ex) { RawLog.Note($"Puls-Abo Versuch {attempt}: {ex.Message}"); }
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        RawLog.Note("Puls-Abo nach Reconnect fehlgeschlagen");
    }

    public ValueTask DisposeAsync()
    {
        if (_measurement is not null) _measurement.ValueChanged -= OnValue;
        if (_device is not null) _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        _service?.Dispose();
        _session?.Dispose();
        _device?.Dispose();
        state.Update(s => s with { StrapConnected = false, HeartRateBpm = null });
        return ValueTask.CompletedTask;
    }
}
