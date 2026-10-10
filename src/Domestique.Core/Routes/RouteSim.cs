using Domestique.Core.Physics;

namespace Domestique.Core.Routes;

// Virtuelle Fahrt: Strecke liefert die Steigung, die Physik die Geschwindigkeit (Review #12).
// Ohne Strecke oder nach dem Ziel geht es flach weiter, damit Speed und Distanz immer stimmen.
public sealed class RouteSim(RiderPhysics physics)
{
    public Route? Route { get; private set; }
    public double DistanceM { get; private set; }
    public RiderPhysics Physics => physics;
    public double SpeedMps => physics.SpeedMps;
    public double RouteDistanceM => DistanceM - _routeStartM;
    public bool Finished => Route is not null && RouteDistanceM >= Route.LengthM;
    public double GradePercent => Route is null || Finished ? 0 : Route.At(RouteDistanceM).GradePercent;
    public double ClimbedM { get; private set; }                         // Höhenmeter bergauf; ohne Strecke kommt nichts dazu
    public TimeSpan MovingTime { get; private set; }                     // Fahrzeit: nur Zeit, in der Watt anliegen
    private double ElevationM => Route is null ? 0 : Route.At(Math.Min(RouteDistanceM, Route.LengthM)).ElevationM;
    private double _routeStartM;

    // Die Gesamtdistanz der Fahrt läuft weiter, die Strecke beginnt bei ihrem Start.
    public void Load(Route route)
    {
        Route = route;
        _routeStartM = DistanceM;
    }

    // „Keine Strecke“: die Fahrt geht flach weiter, Distanz und Geschwindigkeit bleiben erhalten.
    public void Unload() => Route = null;

    public void Step(double powerW, double dt)
    {
        double before = ElevationM;
        DistanceM += physics.Step(powerW, GradePercent, dt);
        ClimbedM += Math.Max(0, ElevationM - before);
        if (powerW > 0) MovingTime += TimeSpan.FromSeconds(dt);
    }
}

// Steigungswechsel am Trainer begrenzen, damit der Widerstand nicht ruckartig springt.
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
