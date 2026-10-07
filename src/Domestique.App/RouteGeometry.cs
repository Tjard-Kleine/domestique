using System.Windows;
using System.Windows.Media;
using Domestique.Core.Routes;

namespace Domestique.App;

// Die Linien werden einmal beim Laden berechnet und eingefroren, danach wandert pro Sekunde nur der Punkt.
// Jeder zehnte Rasterpunkt reicht, also alle 100 m.
internal static class RouteGeometry
{
    // Draufsicht: Längengrade werden mit cos(Breite) gestaucht, damit die Form stimmt.
    public static PointCollection Map(Route r, double w, double h, out Func<RoutePoint, Point> project)
    {
        double k = Math.Cos(r.Points.Average(p => p.Lat) * Math.PI / 180);
        double minX = r.Points.Min(p => p.Lon * k), maxX = r.Points.Max(p => p.Lon * k);
        double minY = r.Points.Min(p => p.Lat), maxY = r.Points.Max(p => p.Lat);
        double scale = Math.Min(w / Math.Max(maxX - minX, 1e-9), h / Math.Max(maxY - minY, 1e-9));
        double offsetX = (w - (maxX - minX) * scale) / 2, offsetY = (h - (maxY - minY) * scale) / 2;   // mittig
        project = p => new Point(offsetX + (p.Lon * k - minX) * scale, h - offsetY - (p.Lat - minY) * scale);
        return Frozen(r, project);
    }

    // Höhenprofil: Distanz nach rechts, Höhe nach oben.
    public static PointCollection Profile(Route r, double w, double h, out Func<RoutePoint, Point> project)
    {
        double minE = r.Points.Min(p => p.ElevationM), maxE = r.Points.Max(p => p.ElevationM);
        double range = Math.Max(maxE - minE, 1), length = r.Points[^1].DistanceM;
        project = p => new Point(p.DistanceM / length * w, h - (p.ElevationM - minE) / range * h);
        return Frozen(r, project);
    }

    private static PointCollection Frozen(Route r, Func<RoutePoint, Point> project)
    {
        var points = new PointCollection(r.Points.Where((_, i) => i % 10 == 0 || i == r.Points.Count - 1).Select(project));
        points.Freeze();
        return points;
    }
}
