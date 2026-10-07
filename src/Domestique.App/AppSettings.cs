using System.IO;
using System.Text.Json;

namespace Domestique.App;

public sealed class AppSettings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public ulong? TrainerAddress { get; set; }
    public ulong? StrapAddress { get; set; }       // Phase 8
    public int FtpW { get; set; } = 250;            // Phase 4
    public double RiderKg { get; set; } = 75;       // Phase 5
    public double BikeKg { get; set; } = 8;         // Phase 5
    public double Difficulty { get; set; } = 0.5;   // Phase 5: 0.5 = halbe Steigung am Trainer

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Domestique", "settings.json");

    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }                     // erster Start oder kaputte Datei
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}