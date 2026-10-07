using System.Globalization;
using System.Xml.Linq;
using Domestique.Core.Recording;
using Xunit;

namespace Domestique.Tests;

public class RecordingTests
{
    private static readonly XNamespace Ns = "http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2";
    private static readonly XNamespace Ext = "http://www.garmin.com/xmlschemas/ActivityExtension/v2";
    private static readonly DateTime Start = new(2026, 10, 7, 16, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Csv_can_be_read_back()
    {
        string path = TempFile("fahrt.csv");
        var samples = Ride(60, cadence: 85.4, hr: 140);
        using (var recorder = new Recorder(path))
            foreach (var s in samples) recorder.Add(s);

        var read = Recorder.ReadCsv(path);
        Assert.Equal(samples.Count, read.Count);
        Assert.Equal(samples[10].TimeUtc, read[10].TimeUtc);
        Assert.Equal(samples[10].PowerW, read[10].PowerW);
        Assert.Equal(85, read[10].CadenceRpm);
        Assert.Equal(140, read[10].HeartRateBpm);
        Assert.Equal(samples[^1].DistanceM, read[^1].DistanceM, 1);
    }

    [Fact]
    public void Csv_uses_dots_even_on_german_windows()
    {
        string path = TempFile("deutsch.csv");
        WithCulture("de-DE", () =>
        {
            using var recorder = new Recorder(path);
            recorder.Add(new Sample(Start, 200, 90, null, 9.25, 1234.5));
        });
        string line = File.ReadAllLines(path)[1];
        Assert.EndsWith(";200;90;;9.25;1234.5", line);
    }

    [Fact]
    public void Truncated_last_line_after_a_crash_is_skipped()
    {
        string path = TempFile("absturz.csv");
        using (var recorder = new Recorder(path))
            foreach (var s in Ride(5)) recorder.Add(s);
        File.AppendAllText(path, "2026-10-07T16:00:05.000");
        Assert.Equal(5, Recorder.ReadCsv(path).Count);
    }

    [Fact]
    public void Tcx_has_totals_power_and_heart_rate()
    {
        var doc = TcxWriter.Build(Ride(601, hr: 150));                       // 10 Minuten mit 200 W
        var lap = doc.Descendants(Ns + "Lap").Single();
        Assert.Equal("600", lap.Element(Ns + "TotalTimeSeconds")!.Value);
        Assert.Equal("6000.0", lap.Element(Ns + "DistanceMeters")!.Value);
        Assert.Equal("120", lap.Element(Ns + "Calories")!.Value);             // 200 W × 600 s = 120 kJ
        Assert.Equal(601, doc.Descendants(Ns + "Trackpoint").Count());
        Assert.All(doc.Descendants(Ext + "Watts"), w => Assert.Equal("200", w.Value));
        Assert.All(doc.Descendants(Ns + "HeartRateBpm"), h => Assert.Equal("150", h.Element(Ns + "Value")!.Value));
        Assert.Empty(doc.Descendants(Ns + "Position"));                        // Indoor: keine GPS-Daten
    }

    [Fact]
    public void Tcx_trackpoint_follows_the_schema_order()
    {
        var point = TcxWriter.Build(Ride(3, cadence: 90, hr: 120)).Descendants(Ns + "Trackpoint").First();
        Assert.Equal(new[] { "Time", "DistanceMeters", "HeartRateBpm", "Cadence", "Extensions" },
                     point.Elements().Select(e => e.Name.LocalName));
    }

    [Fact]
    public void Tcx_leaves_out_cadence_and_heart_rate_when_never_measured()
    {
        var doc = TcxWriter.Build(Ride(10));                                  // z. B. D100 ohne Gurt
        Assert.Empty(doc.Descendants(Ns + "Cadence"));
        Assert.Empty(doc.Descendants(Ns + "HeartRateBpm"));
    }

    [Fact]
    public void Tcx_uses_dots_even_on_german_windows()
    {
        string xml = "";
        WithCulture("de-DE", () => xml = TcxWriter.Build(Ride(3)).ToString());
        Assert.Contains("<ns3:Speed>10.00</ns3:Speed>", xml);
        Assert.DoesNotContain(",", xml);
    }

    [Fact]
    public void Tcx_needs_at_least_two_samples() =>
        Assert.Throws<ArgumentException>(() => TcxWriter.Build(Ride(1)));

    [Fact]
    public void Ride_starts_with_the_first_pedal_stroke()
    {
        var ride = new RideRecorder(Directory.CreateTempSubdirectory().FullName, _ => { });
        ride.Tick(TimeSpan.FromSeconds(1), Start, 0, 0, null, 0, 0);       // steht noch
        Assert.Null(ride.Current);
        ride.Tick(TimeSpan.FromSeconds(2), Start, 150, 0, null, 2, 500);
        Assert.NotNull(ride.Current);
        Assert.Equal(0, ride.Current.Samples[0].DistanceM);                   // Distanz zählt ab Fahrtbeginn
    }

    [Fact]
    public void Ride_records_exactly_one_sample_per_second()
    {
        var ride = new RideRecorder(Directory.CreateTempSubdirectory().FullName, _ => { });
        for (int tick = 0; tick <= 40; tick++)                                 // 10 s im 250-ms-Takt, leicht verzögert
            ride.Tick(TimeSpan.FromMilliseconds(tick * 250 + 3), Start, 200, 90, null, 10, tick * 2.5);
        var times = ride.Current!.Samples.Select(s => s.TimeUtc).ToList();
        Assert.Equal(11, times.Count);
        Assert.Equal(Enumerable.Range(0, 11).Select(i => Start.AddSeconds(i)), times);
    }

    [Fact]
    public void Ride_survives_a_clock_jump()
    {
        var ride = new RideRecorder(Directory.CreateTempSubdirectory().FullName, _ => { });
        ride.Tick(TimeSpan.FromSeconds(0), Start, 200, 0, null, 10, 0);
        ride.Tick(TimeSpan.FromSeconds(1), Start.AddHours(-1), 200, 0, null, 10, 10);   // Uhr springt zurück
        Assert.Equal(Start.AddSeconds(1), ride.Current!.Samples[1].TimeUtc);
    }

    [Fact]
    public void Export_writes_tcx_and_starts_a_new_ride()
    {
        string folder = Directory.CreateTempSubdirectory().FullName;
        var ride = new RideRecorder(folder, _ => { });
        Assert.Null(ride.Export());                                            // noch nichts gefahren
        for (int s = 0; s < 5; s++) ride.Tick(TimeSpan.FromSeconds(s), Start, 200, 0, null, 10, s * 10);
        string tcx = ride.Export()!;
        Assert.True(File.Exists(tcx));
        Assert.Null(ride.Current);
        Assert.Equal("40.0", XDocument.Load(tcx).Descendants(Ns + "Lap").Single().Element(Ns + "DistanceMeters")!.Value);
    }

    [Fact]
    public void Crashed_ride_is_recovered_on_next_start()
    {
        string folder = Directory.CreateTempSubdirectory().FullName;
        var ride = new RideRecorder(folder, _ => { });
        for (int s = 0; s < 5; s++) ride.Tick(TimeSpan.FromSeconds(s), Start, 200, 0, null, 10, s * 10);
        string csv = ride.Current!.CsvPath;
        ride.Current.Dispose();                                                // Absturz: kein Export

        var recovered = RideRecorder.RecoverUnexported(folder, _ => { });
        Assert.Equal(Path.ChangeExtension(csv, ".tcx"), Assert.Single(recovered));
        Assert.Empty(RideRecorder.RecoverUnexported(folder, _ => { }));        // beim zweiten Start nichts mehr zu tun
    }

    // n Sekunden mit 200 W und 10 m/s
    private static List<Sample> Ride(int seconds, double cadence = 0, int? hr = null) =>
        Enumerable.Range(0, seconds)
            .Select(i => new Sample(Start.AddSeconds(i), 200, cadence, hr, 10, i * 10.0))
            .ToList();

    private static string TempFile(string name) => Path.Combine(Directory.CreateTempSubdirectory().FullName, name);

    private static void WithCulture(string name, Action action)
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo(name);
        try { action(); }
        finally { CultureInfo.CurrentCulture = before; }
    }
}
