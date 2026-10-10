using Windows.Devices.Radios;

namespace Domestique.Ble;

// Meldet das Ein- und Ausschalten des Bluetooth-Adapters sofort, statt auf den nächsten Suchversuch zu warten.
public static class BluetoothRadio
{
    // Liefert den Adapter (der Aufrufer muss ihn halten, sonst endet das Ereignis) oder null, wenn es keinen gibt.
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
        catch (Exception) { return null; }                         // ohne Funk-Zugriff greift die Erkennung über die Suche
    }
}
