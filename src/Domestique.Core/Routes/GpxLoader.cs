using System.Globalization;
using System.Xml.Linq;

namespace Domestique.Core.Routes;

// GPX-Verarbeitung in den fünf Schritten aus „Strecken & Physik“.
public static class GpxLoader
{
    public const double StepM = 10;                 // Rasterabstand
    private const double SmoothM = 100;             // Glättungsfenster
    private const double MinGrade = -10, MaxGrade = 20;

    public static Route Load(string xml, string name)
    {
        // 1. Punkte lesen: Track bevorzugt, sonst Route; Namespace egal
        var all = XDocument.Parse(xml).Descendants().ToList();
        var nodes = all.Where(e => e.Name.LocalName == "trkpt").ToList();
        if (nodes.Count == 0) nodes = all.Where(e => e.Name.LocalName == "rtept").ToList();
        if (nodes.Count < 2) throw new FormatException("Die GPX-Datei enthält keine Strecke.");
        var raw = nodes.Select(e => (
            Lat: Num(e.Attribute("lat")?.Value, "Breite"),
            Lon: Num(e.Attribute("lon")?.Value, "Länge"),
            Ele: Num(e.Elements().FirstOrDefault(c => c.Name.LocalName == "ele")?.Value, "Höhe"))).ToList();

        // 2. aufsummierte Distanz
        var dist = new double[raw.Count];
        for (int i = 1; i < raw.Count; i++)
            dist[i] = dist[i - 1] + Haversine(raw[i - 1].Lat, raw[i - 1].Lon, raw[i].Lat, raw[i].Lon);
        double length = dist[^1];
        if (length < 2 * StepM) throw new FormatException("Die Strecke ist kürzer als 20 m.");

        // 3. auf festes Raster umrechnen (linear interpolieren)
        int count = (int)(length / StepM) + 1;
        var grid = new (double D, double Lat, double Lon, double Ele)[count];
        for (int i = 0, j = 0; i < count; i++)
        {
            double d = i * StepM;
            while (j < raw.Count - 2 && dist[j + 1] < d) j++;
            double span = dist[j + 1] - dist[j];
            double f = span > 0 ? (d - dist[j]) / span : 0;
            var (a, b) = (raw[j], raw[j + 1]);
            grid[i] = (d, a.Lat + (b.Lat - a.Lat) * f, a.Lon + (b.Lon - a.Lon) * f, a.Ele + (b.Ele - a.Ele) * f);
        }

        // 4. Höhe glätten (gleitendes Mittel über ±50 m), sonst erzeugt GPS-Rauschen Steigungsspitzen
        int half = (int)(SmoothM / StepM / 2);
        var smooth = new double[count];
        for (int i = 0; i < count; i++)
        {
            int from = Math.Max(0, i - half), to = Math.Min(count - 1, i + half);
            double sum = 0;
            for (int k = from; k <= to; k++) sum += grid[k].Ele;
            smooth[i] = sum / (to - from + 1);
        }

        // 5. Steigung pro Abschnitt, begrenzt
        var points = new RoutePoint[count];
        for (int i = 0; i < count; i++)
        {
            int next = Math.Min(i + 1, count - 1), prev = next - 1;
            double grade = (smooth[next] - smooth[prev]) / StepM * 100;
            points[i] = new RoutePoint(grid[i].D, grid[i].Lat, grid[i].Lon, smooth[i], Math.Clamp(grade, MinGrade, MaxGrade));
        }
        return new Route(name, points, length);
    }

    private static double Num(string? s, string what) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v
            : throw new FormatException($"GPX-Punkt ohne gültige {what}. Strecken ohne Höhendaten gehen nicht.");

    private static double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6_371_000, Rad = Math.PI / 180;
        double dLat = (lat2 - lat1) * Rad, dLon = (lon2 - lon1) * Rad;
        double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(lat1 * Rad) * Math.Cos(lat2 * Rad) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Sqrt(h));
    }
}
