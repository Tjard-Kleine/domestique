namespace Domestique.Core.Routes;

public sealed record RoutePoint(double DistanceM, double Lat, double Lon, double ElevationM, double GradePercent);

// Points lie on the fixed GpxLoader.StepM grid, so a lookup is a division.
public sealed class Route(string name, IReadOnlyList<RoutePoint> points, double lengthM)
{
    public string Name { get; } = name;
    public IReadOnlyList<RoutePoint> Points { get; } = points;
    public double LengthM { get; } = lengthM;

    public RoutePoint At(double distanceM) =>
        Points[Math.Clamp((int)(distanceM / GpxLoader.StepM), 0, Points.Count - 1)];
}
