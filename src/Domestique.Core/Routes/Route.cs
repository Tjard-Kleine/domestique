namespace Domestique.Core.Routes;

public sealed record RoutePoint(double DistanceM, double Lat, double Lon, double ElevationM, double GradePercent);

// Punkte im festen Raster von GpxLoader.StepM, daher reicht für die Suche eine Division.
public sealed class Route(string name, IReadOnlyList<RoutePoint> points, double lengthM)
{
    public string Name { get; } = name;
    public IReadOnlyList<RoutePoint> Points { get; } = points;
    public double LengthM { get; } = lengthM;

    public RoutePoint At(double distanceM) =>
        Points[Math.Clamp((int)(distanceM / GpxLoader.StepM), 0, Points.Count - 1)];
}
