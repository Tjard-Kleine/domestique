using System.Windows;
using System.Windows.Media;
using Domestique.Core.Routes;

namespace Domestique.App;

// Lines are computed and frozen once per route; afterwards only the dot moves each second.
// Every tenth grid point is enough (100 m).
internal static class RouteGeometry
{
    // Top view: longitudes are scaled by cos(latitude) so the shape is right.
    public static PointCollection Map(Route r, double w, double h, out Func<RoutePoint, Point> project)
    {
        double k = Math.Cos(r.Points.Average(p => p.Lat) * Math.PI / 180);
        double minX = r.Points.Min(p => p.Lon * k), maxX = r.Points.Max(p => p.Lon * k);
        double minY = r.Points.Min(p => p.Lat), maxY = r.Points.Max(p => p.Lat);
        double scale = Math.Min(w / Math.Max(maxX - minX, 1e-9), h / Math.Max(maxY - minY, 1e-9));
        double offsetX = (w - (maxX - minX) * scale) / 2, offsetY = (h - (maxY - minY) * scale) / 2;   // centered
        project = p => new Point(offsetX + (p.Lon * k - minX) * scale, h - offsetY - (p.Lat - minY) * scale);
        return Frozen(r, project);
    }

    // Ridden part: line points up to the current position, plus the position itself.
    // The line has a point every 10 grid points, so the count needs no search.
    public static PointCollection Done(PointCollection line, Route r, double distanceM, Func<RoutePoint, Point> project)
    {
        int grid = Math.Clamp((int)(distanceM / GpxLoader.StepM), 0, r.Points.Count - 1);
        int count = Math.Min(grid / 10 + 1, line.Count);
        var done = new PointCollection(count + 1);
        for (int i = 0; i < count; i++) done.Add(line[i]);
        done.Add(project(r.At(distanceM)));
        done.Freeze();
        return done;
    }

    private static PointCollection Frozen(Route r, Func<RoutePoint, Point> project)
    {
        var points = new PointCollection(r.Points.Where((_, i) => i % 10 == 0 || i == r.Points.Count - 1).Select(project));
        points.Freeze();
        return points;
    }
}
