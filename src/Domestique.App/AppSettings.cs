using System.IO;
using System.Text.Json;

namespace Domestique.App;

// A device that was connected before. Kind is the stored key "Trainer" or "Pulsgurt" (heart rate strap).
public sealed record SavedDevice(ulong Address, string Name, string Kind);

// User settings in %APPDATA%\Domestique\settings.json.
public sealed class AppSettings
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Domestique");

    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Docked { get; set; }                // attached to the top edge like a notch
    public ulong? TrainerAddress { get; set; }
    public ulong? StrapAddress { get; set; }
    public List<SavedDevice> Devices { get; set; } = [];   // "My devices" in the Bluetooth settings

    // Updates an existing entry instead of adding a duplicate.
    public void Remember(ulong address, string name, string kind)
    {
        Devices.RemoveAll(d => d.Address == address);
        Devices.Insert(0, new SavedDevice(address, name, kind));
    }
    public int FtpW { get; set; } = 250;            // power zones and avatar mood
    public double RiderKg { get; set; } = 75;
    public double BikeKg { get; set; } = 8;
    public double CdA { get; set; } = 0.32;
    public double Difficulty { get; set; } = 0.5;   // share of the route grade sent to the trainer (Zwift default 50 %)
    public double BackgroundOpacity { get; set; } = 0.88;
    public double Scale { get; set; } = 1.0;
    public bool ShowMap { get; set; } = true;
    public bool NotchDock { get; set; } = true;     // docking at the top edge allowed
    public Dictionary<string, string> Hotkeys { get; set; } = [];        // only changed shortcuts; others use the default
    // Values of the small notch: Power, HeartRate, Cadence, Speed, Distance, Climb, Time
    public List<string> CompactValues { get; set; } = ["Power", "HeartRate", "Cadence", "Speed", "Distance"];

    private static readonly string FilePath = Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }                     // first start or broken file
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
