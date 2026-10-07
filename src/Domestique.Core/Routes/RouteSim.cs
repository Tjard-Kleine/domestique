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
    private double _routeStartM;

    // Die Gesamtdistanz der Fahrt läuft weiter, die Strecke beginnt bei ihrem Start.
    public void Load(Route route)
    {
        Route = route;
        _routeStartM = DistanceM;
    }

    public void Step(double powerW, double dt) => DistanceM += physics.Step(powerW, GradePercent, dt);
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
