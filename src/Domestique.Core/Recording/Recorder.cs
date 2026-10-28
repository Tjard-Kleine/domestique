using System.Globalization;

namespace Domestique.Core.Recording;

public sealed record Sample(DateTime TimeUtc, int PowerW, double CadenceRpm, int? HeartRateBpm, double SpeedMps, double DistanceM);

// Writes every sample to a CSV right away so nothing is lost after a crash. Numbers are culture-invariant.
public sealed class Recorder : IDisposable
{
    private const string Header = "time_utc;power_w;cadence_rpm;hr_bpm;speed_mps;distance_m";
    private readonly List<Sample> _samples = [];
    private readonly StreamWriter _csv;
    public string CsvPath { get; }
    public IReadOnlyList<Sample> Samples => _samples;

    public Recorder(string csvPath)
    {
        CsvPath = csvPath;
        Directory.CreateDirectory(Path.GetDirectoryName(csvPath)!);
        _csv = new StreamWriter(csvPath) { AutoFlush = true };
        _csv.WriteLine(Header);
    }

    public void Add(Sample s)
    {
        _samples.Add(s);
        _csv.WriteLine(FormattableString.Invariant(
            $"{s.TimeUtc:O};{s.PowerW};{s.CadenceRpm:0};{s.HeartRateBpm};{s.SpeedMps:0.00};{s.DistanceM:0.0}"));
    }

    public void Dispose() => _csv.Dispose();

    // Reads a CSV back, e.g. to build the TCX file after a crash.
    public static List<Sample> ReadCsv(string csvPath)
    {
        var samples = new List<Sample>();
        using var reader = new StreamReader(new FileStream(csvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        reader.ReadLine();                                        // header
        while (reader.ReadLine() is { } line)
        {
            var f = line.Split(';');
            if (f.Length < 6
                || !DateTime.TryParse(f[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)
                || !int.TryParse(f[1], CultureInfo.InvariantCulture, out int power)
                || !double.TryParse(f[2], CultureInfo.InvariantCulture, out double cadence)
                || !double.TryParse(f[4], CultureInfo.InvariantCulture, out double speed)
                || !double.TryParse(f[5], CultureInfo.InvariantCulture, out double distance))
                continue;                                         // e.g. a truncated last line after a crash
            int? hr = int.TryParse(f[3], CultureInfo.InvariantCulture, out int h) ? h : null;
            samples.Add(new Sample(time, power, cadence, hr, speed, distance));
        }
        return samples;
    }
}
