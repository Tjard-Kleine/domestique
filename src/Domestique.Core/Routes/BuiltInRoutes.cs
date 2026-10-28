using System.Globalization;
using System.Text;

namespace Domestique.Core.Routes;

// Virtual training routes shipped with the app. They are generated as GPX and go through the same loader as user
// routes. Shapes are made up; the coordinates are not a real place.
public static class BuiltInRoutes
{
    private const double OriginLat = 47.2, OriginLon = 11.4, KmPerDegree = 6371 * Math.PI / 180;

    private sealed record Course(string Name, double LengthKm, Func<double, (double X, double Y)> Shape, Func<double, double> Elevation);

    private static readonly Course[] Courses =
    [
        // flat with gentle waves, below 2 %
        new("Flatland Loop", 20,
            t => (Math.Cos(2 * Math.PI * t) + 0.18 * Math.Sin(6 * Math.PI * t), 0.6 * Math.Sin(2 * Math.PI * t)),
            d => 50 + 6 * Math.Sin(2 * Math.PI * d / 5000) + 3 * Math.Sin(2 * Math.PI * d / 1700)),
        // three climbs of 4 to 6 %
        new("Rolling Hills", 32,
            t => (Math.Cos(2 * Math.PI * t) + 0.25 * Math.Cos(6 * Math.PI * t), Math.Sin(2 * Math.PI * t) + 0.2 * Math.Sin(4 * Math.PI * t)),
            d => 200 + Hill(d, 6000, 90, 1500) + Hill(d, 15000, 130, 1900) + Hill(d, 25000, 70, 1100)),
        // summit finish on switchbacks, 7 % on average
        new("Summit Finish", 14,
            t => (0.6 * Math.Sin(2 * Math.PI * 7 * t), 4 * t),
            d => 600 + 980 * d / 14000 + 8 * Math.Sin(2 * Math.PI * d / 2000)),
        // nearly flat oval
        new("Time Trial Oval", 30,
            t => (Math.Cos(2 * Math.PI * t), 0.45 * Math.Sin(2 * Math.PI * t)),
            d => 30 + 2 * Math.Sin(2 * Math.PI * d / 7500)),
    ];

    public static IReadOnlyList<string> Names { get; } = Courses.Select(c => c.Name).ToList();

    public static double LengthKm(string name) => Find(name).LengthKm;

    public static Route Load(string name) => GpxLoader.Load(Gpx(name), name);

    // A point every 20 m, shape scaled to the target length.
    private static string Gpx(string name)
    {
        var c = Find(name);
        const int Samples = 4000;
        var raw = Enumerable.Range(0, Samples + 1).Select(i => c.Shape((double)i / Samples)).ToArray();
        double rawLength = 0;
        for (int i = 1; i < raw.Length; i++) rawLength += Distance(raw[i - 1], raw[i]);
        double scale = c.LengthKm / rawLength;

        var gpx = new StringBuilder("<gpx version=\"1.1\" creator=\"Domestique\"><trk><name>").Append(c.Name).Append("</name><trkseg>");
        double walked = 0, next = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            if (i > 0) walked += Distance(raw[i - 1], raw[i]) * scale;
            if (walked + 1e-9 < next && i < raw.Length - 1) continue;
            next = walked + 0.02;
            double lat = OriginLat + raw[i].Y * scale / KmPerDegree;
            double lon = OriginLon + raw[i].X * scale / (KmPerDegree * Math.Cos(OriginLat * Math.PI / 180));
            gpx.Append(string.Format(CultureInfo.InvariantCulture, "<trkpt lat=\"{0:0.0000000}\" lon=\"{1:0.0000000}\"><ele>{2:0.0}</ele></trkpt>",
                lat, lon, c.Elevation(walked * 1000)));
        }
        return gpx.Append("</trkseg></trk></gpx>").ToString();
    }

    private static Course Find(string name) =>
        Array.Find(Courses, c => c.Name == name) ?? throw new ArgumentException($"Unknown route: {name}", nameof(name));

    private static double Hill(double d, double at, double height, double width) => height * Math.Exp(-Math.Pow((d - at) / width, 2));

    private static double Distance((double X, double Y) a, (double X, double Y) b) => Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
}
