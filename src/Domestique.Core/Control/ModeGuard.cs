using Domestique.Core.Protocol;

namespace Domestique.Core.Control;

public enum ControlMode { Free, Erg, Sim }

// Keeps ERG and simulation apart: in ERG no grade is ever sent, and unchanged targets are not resent.
public sealed class ModeGuard(Action<byte[]> sendTarget)
{
    private double? _lastGrade;
    public ControlMode Mode { get; private set; } = ControlMode.Free;
    public int? TargetPowerW { get; private set; }
    public (int Min, int Max) PowerLimits { get; set; } = (0, 2000);

    public void SetErg(int watts)
    {
        watts = Math.Clamp(watts, PowerLimits.Min, PowerLimits.Max);
        Mode = ControlMode.Erg;
        _lastGrade = null;
        if (TargetPowerW == watts) return;
        TargetPowerW = watts;
        sendTarget(FtmsCommands.SetTargetPower(watts));
    }

    // Back to simulation with the grade the trainer should have now (flat without a route).
    public void EndErg(double gradePercent = 0)
    {
        if (Mode != ControlMode.Erg) return;
        Mode = ControlMode.Free;
        TargetPowerW = null;
        SetGrade(gradePercent);
    }

    public void SetGrade(double gradePercent)
    {
        if (Mode == ControlMode.Erg) return;
        Mode = ControlMode.Sim;
        if (_lastGrade is double last && Math.Abs(last - gradePercent) < 0.1) return;
        _lastGrade = gradePercent;
        sendTarget(FtmsCommands.SetSimulation(gradePercent));
    }

    // After (re)connecting: resends the current target; without a mode the road is flat.
    public void Reapply()
    {
        if (Mode == ControlMode.Erg && TargetPowerW is int w) sendTarget(FtmsCommands.SetTargetPower(w));
        else if (Mode == ControlMode.Sim) sendTarget(FtmsCommands.SetSimulation(_lastGrade ?? 0));
        else SetGrade(0);
    }
}
