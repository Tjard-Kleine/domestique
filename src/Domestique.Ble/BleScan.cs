using Windows.Devices.Bluetooth.Advertisement;

namespace Domestique.Ble;

public static class BleScan
{
    // Liefert die Adresse des ersten Geräts, das den Service anbietet, oder null nach Ablauf der Zeit.
    public static async Task<ulong?> FindFirstAsync(Guid serviceUuid, TimeSpan timeout)
    {
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(serviceUuid);
        var found = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Received += (_, e) => found.TrySetResult(e.BluetoothAddress);
        watcher.Start();
        try
        {
            var first = await Task.WhenAny(found.Task, Task.Delay(timeout));
            return first == found.Task ? found.Task.Result : (ulong?)null;
        }
        finally { watcher.Stop(); }
    }
}