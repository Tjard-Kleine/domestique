using System.Globalization;
using System.Text;
using Domestique.Core.Physics;
using Domestique.Core.Routes;
using Xunit;

namespace Domestique.Tests;

public class RouteTests
{
    private const double MetersPerDegreeLat = 6_371_000 * Math.PI / 180;

    [Fact]
    public void One_kilometer_at_five_percent()
    {
        const string gpx = """
            <gpx xmlns="http://www.topografix.com/GPX/1/1"><trk><trkseg>
              <trkpt lat="51.0" lon="7.0"><ele>100</ele></trkpt>
              <trkpt lat="51.0089932" lon="7.0"><ele>150</ele></trkpt>
            </trkseg></trk></gpx>
            """;
        var route = GpxLoader.Load(gpx, "Test");
        Assert.InRange(route.LengthM, 999, 1001);
        Assert.InRange(route.At(500).GradePercent, 4.9, 5.1);
    }

    [Fact]
    public void Reads_route_points_when_there_is_no_track()
    {
        var route = GpxLoader.Load(Gpx("rte", "rtept", Line(1000, d => 100)), "Route");
        Assert.InRange(route.LengthM, 999, 1001);
    }

    [Fact]
    public void Prefers_track_over_route_points()
    {
        string both = Gpx("trk><trkseg", "trkpt", Line(1000, d => 100)).Replace("</gpx>", "")
                    + "<rte>" + string.Concat(Line(3000, d => 100).Select(p => Point("rtept", p))) + "</rte></gpx>";
        Assert.InRange(GpxLoader.Load(both, "Beides").LengthM, 999, 1001);
    }

    [Fact]
    public void Rejects_route_without_elevation()
    {
        const string gpx = """<gpx><trk><trkseg><trkpt lat="51" lon="7"/><trkpt lat="51.01" lon="7"/></trkseg></trk></gpx>""";
        Assert.Throws<FormatException>(() => GpxLoader.Load(gpx, "Ohne Höhe"));
    }

    [Fact]
    public void Rejects_file_without_points() =>
        Assert.Throws<FormatException>(() => GpxLoader.Load("<gpx></gpx>", "Leer"));

    [Fact]
    public void Smooths_gps_noise()
    {
        // 10 m Ausreißer auf flacher Strecke: roh wären das 100 % Steigung
        var route = GpxLoader.Load(Gpx("trk><trkseg", "trkpt", Line(1000, d => Math.Abs(d - 500) < 1 ? 110 : 100)), "Rauschen");
        double max = route.Points.Max(p => Math.Abs(p.GradePercent));
        Assert.InRange(max, 5, 10);
    }

    [Fact]
    public void Clamps_extreme_grades()
    {
        var route = GpxLoader.Load(Gpx("trk><trkseg", "trkpt", Line(1000, d => d > 500 ? 400 : 100)), "Wand");
        Assert.All(route.Points, p => Assert.InRange(p.GradePercent, -10, 20));
    }

    [Fact]
    public void Simulation_follows_the_route_grade()
    {
        // 500 m flach, dann 500 m mit 5 %
        var route = GpxLoader.Load(Gpx("trk><trkseg", "trkpt", Line(1000, d => d <= 500 ? 100 : 100 + (d - 500) * 0.05)), "Anstieg");
        var sim = new RouteSim(new RiderPhysics());
        sim.Load(route);
        Assert.InRange(sim.GradePercent, -0.5, 0.5);
        while (sim.RouteDistanceM < 750) sim.Step(250, 0.25);
        Assert.InRange(sim.GradePercent, 4.5, 5.5);
    }

    [Fact]
    public void After_the_finish_the_ride_continues_flat()
    {
        var route = GpxLoader.Load(Gpx("trk><trkseg", "trkpt", Line(200, d => 100 + d * 0.05)), "Kurz");
        var sim = new RouteSim(new RiderPhysics());
        sim.Load(route);
        while (!sim.Finished) sim.Step(300, 0.25);
        double distance = sim.DistanceM;
        for (int i = 0; i < 40; i++) sim.Step(200, 0.25);
        Assert.Equal(0, sim.GradePercent);
        Assert.True(sim.DistanceM > distance + 10);
    }

    [Fact]
    public void Route_loaded_mid_ride_starts_at_its_beginning()
    {
        var sim = new RouteSim(new RiderPhysics());
        for (int i = 0; i < 400; i++) sim.Step(200, 0.25);                  // ohne Strecke: flach
        double ridden = sim.DistanceM;
        sim.Load(GpxLoader.Load(Gpx("trk><trkseg", "trkpt", Line(1000, d => 100)), "Später"));
        Assert.Equal(0, sim.RouteDistanceM);
        Assert.Equal(ridden, sim.DistanceM);                                 // Gesamtdistanz der Fahrt bleibt
    }

    [Fact]
    public void No_route_after_unload_rides_flat_and_keeps_distance()
    {
        var sim = new RouteSim(new RiderPhysics());
        sim.Load(BuiltInRoutes.Load("Bergankunft"));
        for (int i = 0; i < 400; i++) sim.Step(250, 0.25);
        double ridden = sim.DistanceM;
        sim.Unload();
        Assert.Null(sim.Route);
        Assert.Equal(0, sim.GradePercent);
        Assert.False(sim.Finished);
        Assert.Equal(ridden, sim.DistanceM);
    }

    [Theory]
    [InlineData("Flachland-Runde", 2.5)]
    [InlineData("Hügelland", 7)]
    [InlineData("Bergankunft", 11)]
    [InlineData("Zeitfahr-Oval", 1)]
    public void Built_in_routes_have_their_length_and_grades(string name, double maxGrade)
    {
        var route = BuiltInRoutes.Load(name);
        Assert.InRange(route.LengthM, BuiltInRoutes.LengthKm(name) * 990, BuiltInRoutes.LengthKm(name) * 1010);
        Assert.All(route.Points, p => Assert.InRange(p.GradePercent, -maxGrade, maxGrade));
    }

    [Fact]
    public void Mountain_finish_climbs_seven_percent_on_average()
    {
        var route = BuiltInRoutes.Load("Bergankunft");
        double climb = (route.Points[^1].ElevationM - route.Points[0].ElevationM) / route.LengthM * 100;
        Assert.InRange(climb, 6.3, 7.7);
    }

    [Fact]
    public void Counts_climbed_meters_of_the_route()
    {
        var sim = new RouteSim(new RiderPhysics());
        sim.Load(BuiltInRoutes.Load("Bergankunft"));
        while (!sim.Finished) sim.Step(300, 1);
        Assert.InRange(sim.ClimbedM, 940, 1000);                           // 600 → 1580 m
        double atTop = sim.ClimbedM;
        for (int i = 0; i < 60; i++) sim.Step(300, 1);                      // nach dem Ziel flach weiter
        Assert.Equal(atTop, sim.ClimbedM);
    }

    [Fact]
    public void Flat_route_and_no_route_add_almost_no_climb()
    {
        var sim = new RouteSim(new RiderPhysics());
        for (int i = 0; i < 600; i++) sim.Step(250, 1);                     // ohne Strecke
        Assert.Equal(0, sim.ClimbedM);
        sim.Load(BuiltInRoutes.Load("Zeitfahr-Oval"));
        while (!sim.Finished) sim.Step(250, 1);
        Assert.InRange(sim.ClimbedM, 0, 25);
    }

    [Fact]
    public void Moving_time_counts_only_while_pedaling()
    {
        var sim = new RouteSim(new RiderPhysics());
        for (int i = 0; i < 40; i++) sim.Step(200, 0.25);                   // 10 s treten
        for (int i = 0; i < 40; i++) sim.Step(0, 0.25);                     // 10 s rollen oder stehen
        Assert.Equal(TimeSpan.FromSeconds(10), sim.MovingTime);
    }

    [Fact]
    public void Grade_limiter_changes_at_most_one_percent_per_second()
    {
        var limiter = new GradeLimiter();
        Assert.Equal(1, limiter.Next(8, 1));
        Assert.Equal(2, limiter.Next(8, 1));
        Assert.Equal(2.25, limiter.Next(8, 0.25));
        Assert.Equal(1.25, limiter.Next(-5, 1));
    }

    // Punkte nach Norden im 10-m-Abstand, Höhe als Funktion der Distanz
    private static IEnumerable<(double Lat, double Lon, double Ele)> Line(double lengthM, Func<double, double> elevation)
    {
        for (double d = 0; d <= lengthM + 0.001; d += 10)
            yield return (51 + d / MetersPerDegreeLat, 7, elevation(d));
    }

    private static string Gpx(string container, string point, IEnumerable<(double Lat, double Lon, double Ele)> points)
    {
        var sb = new StringBuilder($"""<gpx xmlns="http://www.topografix.com/GPX/1/1"><{container}>""");
        foreach (var p in points) sb.Append(Point(point, p));
        string close = container.Contains("trkseg") ? "</trkseg></trk>" : $"</{container}>";
        return sb.Append(close).Append("</gpx>").ToString();
    }

    private static string Point(string name, (double Lat, double Lon, double Ele) p) => string.Format(CultureInfo.InvariantCulture,
        """<{0} lat="{1:0.0000000}" lon="{2}"><ele>{3:0.0}</ele></{0}>""", name, p.Lat, p.Lon, p.Ele);
}
