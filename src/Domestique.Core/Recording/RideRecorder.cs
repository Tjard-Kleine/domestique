namespace Domestique.Core.Recording;

// One ride: starts with the first pedal stroke, records exactly one sample per second and exports a TCX file.
// Time comes in as a monotonic span (Stopwatch) so clock jumps don't shift anything.
public sealed class RideRecorder(string folder, Action<string>? log = null)
{
    private DateTime _startUtc;
    private TimeSpan _startAt;
    private double _startDistanceM;
    private int _lastSecond;
    public Recorder? Current { get; private set; }

    private void Log(string text) => (log ?? RawLog.Note)(text);

    public void Tick(TimeSpan now, DateTime utcNow, int powerW, double cadenceRpm, int? heartRateBpm, double speedMps, double distanceM)
    {
        if (Current is null)
        {
            if (powerW <= 0) return;
            _startUtc = utcNow;
            _startAt = now;
            _startDistanceM = distanceM;
            _lastSecond = -1;
            Current = new Recorder(Path.Combine(folder, $"{utcNow.ToLocalTime():yyyy-MM-dd_HH-mm-ss}.csv"));
            Log("Recording started: " + Current.CsvPath);
        }
        int second = (int)(now - _startAt).TotalSeconds;
        if (second <= _lastSecond) return;
        _lastSecond = second;
        Current.Add(new Sample(_startUtc.AddSeconds(second), powerW, cadenceRpm, heartRateBpm, speedMps, distanceM - _startDistanceM));
    }

    // Writes the TCX file and ends the ride; null until there are at least two samples.
    public string? Export()
    {
        if (Current is null || Current.Samples.Count < 2) return null;
        var recorder = Current;
        Current = null;
        recorder.Dispose();
        string tcx = Path.ChangeExtension(recorder.CsvPath, ".tcx");
        TcxWriter.Write(tcx, recorder.Samples);
        Log("Ride exported: " + tcx);
        return tcx;
    }

    // After a crash only the CSV exists: builds the missing TCX files and returns them.
    public static List<string> RecoverUnexported(string folder, Action<string>? log = null)
    {
        var recovered = new List<string>();
        if (!Directory.Exists(folder)) return recovered;
        foreach (var csv in Directory.GetFiles(folder, "*.csv"))
        {
            string tcx = Path.ChangeExtension(csv, ".tcx");
            if (File.Exists(tcx)) continue;
            try
            {
                var samples = Recorder.ReadCsv(csv);
                if (samples.Count < 2) continue;
                TcxWriter.Write(tcx, samples);
                recovered.Add(tcx);
                (log ?? RawLog.Note)("Ride recovered after crash: " + tcx);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                (log ?? RawLog.Note)($"Recovering {csv} failed: {ex.Message}");
            }
        }
        return recovered;
    }
}
