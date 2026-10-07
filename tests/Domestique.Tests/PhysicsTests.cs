using Domestique.Core.Physics;
using Xunit;

namespace Domestique.Tests;

public class PhysicsTests
{
    // Kontrollwerte aus „Strecken & Physik“
    [Theory]
    [InlineData(200, 0, 33.9)]
    [InlineData(250, 5, 17.9)]
    public void Reaches_expected_speed(double watts, double grade, double expectedKmh)
    {
        var physics = new RiderPhysics();
        for (int i = 0; i < 2400; i++) physics.Step(watts, grade, 0.25);   // 10 Minuten fahren
        Assert.InRange(physics.SpeedMps * 3.6, expectedKmh - 0.3, expectedKmh + 0.3);
    }

    [Fact]
    public void Standing_uphill_without_power_does_not_roll_back()
    {
        var physics = new RiderPhysics();
        for (int i = 0; i < 40; i++) physics.Step(0, 5, 0.25);
        Assert.Equal(0, physics.SpeedMps);
    }

    [Fact]
    public void Rolls_downhill_without_power()
    {
        var physics = new RiderPhysics();
        for (int i = 0; i < 40; i++) physics.Step(0, -5, 0.25);
        Assert.True(physics.SpeedMps > 3);
    }

    [Fact]
    public void Coasts_down_after_stopping_to_pedal()
    {
        var physics = new RiderPhysics();
        for (int i = 0; i < 2400; i++) physics.Step(200, 0, 0.25);
        double cruising = physics.SpeedMps;
        for (int i = 0; i < 20; i++) physics.Step(0, 0, 0.25);             // 5 s ausrollen
        Assert.InRange(physics.SpeedMps, 1, cruising - 1);                  // langsamer, aber nicht sofort stehen
    }

    [Fact]
    public void Broken_mass_setting_cannot_break_the_physics()
    {
        var physics = new RiderPhysics { MassKg = 0 };
        physics.Step(200, 0, 0.25);
        Assert.True(double.IsFinite(physics.SpeedMps));
    }
}
