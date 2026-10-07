namespace Domestique.Core.Physics;

// Leistungsbilanz aus „Strecken & Physik“, integriert über die Bewegungsenergie: stabil im Stand,
// realistisches Beschleunigen und Ausrollen. Alle Werte in SI-Einheiten.
public sealed class RiderPhysics
{
    private const double G = 9.81;
    private double _massKg = 83;
    public double MassKg { get => _massKg; set => _massKg = Math.Max(value, 30); }   // Fahrer + Rad, nie 0
    public double CdA { get; set; } = 0.32;
    public double Crr { get; init; } = 0.004;                     // wie Zwift
    public double Rho { get; init; } = 1.225;
    public double Efficiency { get; init; } = 0.97;
    public double SpeedMps { get; private set; }

    public double Force(double v, double gradePercent)
    {
        double theta = Math.Atan(gradePercent / 100);
        return MassKg * G * Math.Sin(theta)
             + Crr * MassKg * G * Math.Cos(theta)
             + 0.5 * Rho * CdA * v * v;
    }

    // Rechnet dt Sekunden weiter und gibt die gefahrene Strecke in Metern zurück.
    // Mindestens 0,5 m/s in der Bilanz, damit man aus dem Stand losrollt, bergab auch ohne Treten.
    public double Step(double powerW, double gradePercent, double dt)
    {
        double v = SpeedMps;
        double energy = 0.5 * MassKg * v * v + dt * (Math.Max(powerW, 0) * Efficiency - Force(v, gradePercent) * Math.Max(v, 0.5));
        SpeedMps = Math.Sqrt(2 * Math.Max(0, energy) / MassKg);
        return SpeedMps * dt;
    }
}
