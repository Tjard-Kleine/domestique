using Domestique.Core.Physics;

namespace Domestique.Core.Routes;

// Virtual ride: the route gives the grade, the physics the speed. Without a route or after the finish
// the ride continues flat, so speed and distance are always valid.
public sealed class RouteSim(RiderPhysics physics)
{
    public Route? Route { get; private set; }
    public double DistanceM { get; private set; }
    public RiderPhysics Physics => physics;
    public double SpeedMps => physics.SpeedMps;
    public double RouteDistanceM => DistanceM - _routeStartM;
    public bool Finished => Route is not null && RouteDistanceM >= Route.LengthM;
    public double GradePercent => Route is null || Finished ? 0 : Route.At(RouteDistanceM).GradePercent;
    public double ClimbedM { get; private set; }                         // elevation gain; nothing is added without a route
    public TimeSpan MovingTime { get; private set; }                     // counts only while there is power
    private double ElevationM => Route is null ? 0 : Route.At(Math.Min(RouteDistanceM, Route.LengthM)).ElevationM;
    private double _routeStartM;

    // The total ride distance keeps counting; the route starts at its beginning.
    public void Load(Route route)
    {
        Route = route;
        _routeStartM = DistanceM;
    }

    public void Unload() => Route = null;

    public void Step(double powerW, double dt)
    {
        double before = ElevationM;
        DistanceM += physics.Step(powerW, GradePercent, dt);
        ClimbedM += Math.Max(0, ElevationM - before);
        if (powerW > 0) MovingTime += TimeSpan.FromSeconds(dt);
    }
}

// Limits grade changes on the trainer so the resistance doesn't jump.
public sealed class GradeLimiter(double maxPercentPerSecond = 1.0)
{
    public double Current { get; private set; }

    public double Next(double wanted, double dtSeconds)
    {
        double max = maxPercentPerSecond * dtSeconds;
        Current += Math.Clamp(wanted - Current, -max, max);
        return Current;
    }
}
