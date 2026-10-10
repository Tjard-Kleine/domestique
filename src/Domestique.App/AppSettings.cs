using System.IO;
using System.Text.Json;

namespace Domestique.App;

// Ein Gerät, das schon einmal verbunden war. Kind: "Trainer" oder "Pulsgurt".
public sealed record SavedDevice(ulong Address, string Name, string Kind);

public sealed class AppSettings
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Domestique");

    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Docked { get; set; }                // hängt gerade oben wie eine Notch
    public ulong? TrainerAddress { get; set; }
    public ulong? StrapAddress { get; set; }
    public List<SavedDevice> Devices { get; set; } = [];   // „Meine Geräte“ in den Bluetooth-Einstellungen

    // Merkt sich ein verbundenes Gerät. Ein vorhandener Eintrag wird aktualisiert, nicht verdoppelt.
    public void Remember(ulong address, string name, string kind)
    {
        Devices.RemoveAll(d => d.Address == address);
        Devices.Insert(0, new SavedDevice(address, name, kind));
    }
    public int FtpW { get; set; } = 250;            // für Leistungszonen und den Avatar
    public double RiderKg { get; set; } = 75;
    public double BikeKg { get; set; } = 8;
    public double CdA { get; set; } = 0.32;
    public double Difficulty { get; set; } = 0.5;   // 0.5 = halbe Steigung am Trainer, wie Zwifts Standard
    public string Theme { get; set; } = "Standard"; // Standard, Tour oder Vintage
    public double BackgroundOpacity { get; set; } = 0.88;
    public double Scale { get; set; } = 1.0;
    public bool ShowMap { get; set; } = true;
    public bool NotchDock { get; set; } = true;     // am oberen Rand andocken erlaubt

    private static readonly string FilePath = Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }                     // erster Start oder kaputte Datei
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
