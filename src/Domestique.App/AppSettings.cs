using System.IO;
using System.Text.Json;

namespace Domestique.App;

public sealed class AppSettings
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Domestique");

    public double? Left { get; set; }
    public double? Top { get; set; }
    public ulong? TrainerAddress { get; set; }
    public ulong? StrapAddress { get; set; }
    public int FtpW { get; set; } = 250;            // für Workouts (Phase 4, derzeit nicht umgesetzt)
    public double RiderKg { get; set; } = 75;
    public double BikeKg { get; set; } = 8;
    public double CdA { get; set; } = 0.32;
    public double Difficulty { get; set; } = 0.5;   // 0.5 = halbe Steigung am Trainer, wie Zwifts Standard
    public string BackgroundColor { get; set; } = "#000000";
    public string TextColor { get; set; } = "#FFFFFF";
    public string AccentColor { get; set; } = "#FF5A36";
    public double BackgroundOpacity { get; set; } = 0.6;
    public double Scale { get; set; } = 1.0;
    public bool ShowMap { get; set; } = true;

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
