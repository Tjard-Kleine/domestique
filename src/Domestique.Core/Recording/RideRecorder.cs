namespace Domestique.Core.Recording;

// Eine Fahrt: startet mit dem ersten Tritt, schreibt genau einen Messpunkt pro Sekunde und erzeugt beim Export
// die TCX-Datei. Die Zeit kommt als monotone Zeitspanne (Stopwatch) herein, damit Uhrzeitsprünge nichts verschieben.
public sealed class RideRecorder(string folder, Action<string>? log = null)
{
    private DateTime _startUtc;
    private TimeSpan _startAt;
    private double _startDistanceM;
    private int _lastSecond;
    public Recorder? Current { get; private set; }

    public TimeSpan? Elapsed(TimeSpan now) => Current is null ? null : now - _startAt;

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
            Log("Aufzeichnung gestartet: " + Current.CsvPath);
        }
        int second = (int)(now - _startAt).TotalSeconds;
        if (second <= _lastSecond) return;
        _lastSecond = second;
        Current.Add(new Sample(_startUtc.AddSeconds(second), powerW, cadenceRpm, heartRateBpm, speedMps, distanceM - _startDistanceM));
    }

    // Schreibt die TCX-Datei und beendet die Fahrt. null, solange noch keine zwei Messpunkte da sind.
    public string? Export()
    {
        if (Current is null || Current.Samples.Count < 2) return null;
        var recorder = Current;
        Current = null;                                           // die nächste Fahrt beginnt neu
        recorder.Dispose();
        string tcx = Path.ChangeExtension(recorder.CsvPath, ".tcx");
        TcxWriter.Write(tcx, recorder.Samples);
        Log("Fahrt exportiert: " + tcx);
        return tcx;
    }

    // Nach einem Absturz liegt nur die CSV da: daraus die TCX-Datei erzeugen. Liefert die geretteten Dateien.
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
                (log ?? RawLog.Note)("Fahrt nach Absturz gerettet: " + tcx);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                (log ?? RawLog.Note)($"Rettung von {csv} fehlgeschlagen: {ex.Message}");
            }
        }
        return recovered;
    }
}
