using Domestique.Core.Protocol;

namespace Domestique.Core.Control;

public enum ControlMode { Free, Erg, Sim }

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
        if (TargetPowerW == watts) return;               // nichts Neues: nichts senden
        TargetPowerW = watts;
        sendTarget(FtmsCommands.SetTargetPower(watts));
    }

    // Zurück in die Simulation, mit der Steigung, die gerade am Trainer anliegen soll (ohne Strecke flach).
    public void EndErg(double gradePercent = 0)
    {
        if (Mode != ControlMode.Erg) return;
        Mode = ControlMode.Free;
        TargetPowerW = null;
        SetGrade(gradePercent);
    }

    public void SetGrade(double gradePercent)
    {
        if (Mode == ControlMode.Erg) return;              // im ERG nie eine Steigung senden
        Mode = ControlMode.Sim;
        if (_lastGrade is double last && Math.Abs(last - gradePercent) < 0.1) return;
        _lastGrade = gradePercent;
        sendTarget(FtmsCommands.SetSimulation(gradePercent));
    }

    // Nach dem (Wieder-)Verbinden: aktuellen Zielwert erneut senden. Ohne Modus gilt flache Straße.
    public void Reapply()
    {
        if (Mode == ControlMode.Erg && TargetPowerW is int w) sendTarget(FtmsCommands.SetTargetPower(w));
        else if (Mode == ControlMode.Sim) sendTarget(FtmsCommands.SetSimulation(_lastGrade ?? 0));
        else SetGrade(0);
    }
}