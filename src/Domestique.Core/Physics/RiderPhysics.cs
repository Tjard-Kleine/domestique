namespace Domestique.Core.Physics;

// Power balance integrated over kinetic energy: stable when standing, realistic acceleration and coasting. SI units.
public sealed class RiderPhysics
{
    private const double G = 9.81;
    private double _massKg = 83;
    public double MassKg { get => _massKg; set => _massKg = Math.Max(value, 30); }   // rider + bike, never 0
    public double CdA { get; set; } = 0.32;
    public double Crr { get; init; } = 0.004;                     // same as Zwift
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

    // Advances dt seconds and returns the distance in meters. Uses at least 0.5 m/s in the balance
    // so the rider starts rolling from standstill, and downhill without pedaling.
    public double Step(double powerW, double gradePercent, double dt)
    {
        double v = SpeedMps;
        double energy = 0.5 * MassKg * v * v + dt * (Math.Max(powerW, 0) * Efficiency - Force(v, gradePercent) * Math.Max(v, 0.5));
        SpeedMps = Math.Sqrt(2 * Math.Max(0, energy) / MassKg);
        return SpeedMps * dt;
    }
}
