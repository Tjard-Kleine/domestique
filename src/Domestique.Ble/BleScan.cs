using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

namespace Domestique.Ble;

public static class BleScan
{
    // Liefert die Adresse des ersten Geräts, das den Service anbietet, oder null nach Ablauf der Zeit.
    public static async Task<ulong?> FindFirstAsync(Guid serviceUuid, TimeSpan timeout)
    {
        var found = await ScanAsync(serviceUuid, timeout, stopAtFirst: true);
        return found.Count > 0 ? found[0].Address : null;
    }

    // Alle Geräte in Reichweite. filter = null zeigt auch Trainer, die ihren Service nicht ankündigen.
    public static Task<IReadOnlyList<(ulong Address, string Name)>> ScanAllAsync(Guid? filter, TimeSpan duration) =>
        ScanAsync(filter, duration, stopAtFirst: false);

    private static async Task<IReadOnlyList<(ulong Address, string Name)>> ScanAsync(Guid? filter, TimeSpan duration, bool stopAtFirst)
    {
        var found = new Dictionary<ulong, string>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        if (filter is Guid f) watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(f);
        watcher.Received += (_, e) =>
        {
            lock (found)
            {
                if (!found.TryGetValue(e.BluetoothAddress, out var name) || name.Length == 0)
                    found[e.BluetoothAddress] = e.Advertisement.LocalName;   // Name kommt oft erst mit der Scan-Antwort
            }
            if (stopAtFirst) done.TrySetResult();
        };
        watcher.Stopped += (_, e) =>                                      // z. B. Bluetooth ausgeschaltet
        {
            if (e.Error != BluetoothError.Success)
                done.TrySetException(new InvalidOperationException($"Bluetooth-Suche abgebrochen ({e.Error}). Ist Bluetooth an?"));
        };
        watcher.Start();
        try { await Task.WhenAny(done.Task, Task.Delay(duration)); }
        finally { watcher.Stop(); }
        if (done.Task.IsFaulted) throw done.Task.Exception!.InnerException!;
        lock (found)
            return found.Select(kv => (kv.Key, kv.Value.Length == 0 ? "Unbenannt" : kv.Value)).ToList();
    }
}
