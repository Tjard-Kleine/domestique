using Windows.Devices.Radios;

namespace Domestique.Ble;

// Reports the Bluetooth adapter being switched on or off right away instead of waiting for the next scan.
public static class BluetoothRadio
{
    // Returns the radio (the caller must keep a reference, otherwise the event stops) or null without one.
    public static async Task<Radio?> WatchAsync(Action<bool> onChanged)
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return null;
            var radio = (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            if (radio is null) return null;
            radio.StateChanged += (r, _) => onChanged(r.State == RadioState.On);
            return radio;
        }
        catch (Exception) { return null; }                         // without radio access, scans detect Bluetooth being off
    }
}
