namespace Domestique.Core.Protocol;

public static class FtmsCommands
{
    public static byte[] RequestControl() => [0x00];
    public static byte[] Start() => [0x07];
    public static byte[] Stop() => [0x08, 0x01]; //first - opcode ,second - parameter
    public static byte[] Pause() => [0x08, 0x02];

    public static byte[] SetTargetPower(int watts)
    {
        short w = (short)Math.Clamp(watts, 0, short.MaxValue);
        return [0x05, (byte)w, (byte)(w >> 8)];
    }

    public static byte[] SetSimulation(double gradePercent, double crr = 0.004, double cwKgPerM = 0.51)
    {
        short grade = (short)Math.Round(gradePercent * 100);     // Einheit 0,01 %
        return [0x11, 0x00, 0x00,                                 // Wind 0
                (byte)grade, (byte)(grade >> 8),
                (byte)Math.Round(crr * 10000),                    // Einheit 0,0001
                (byte)Math.Round(cwKgPerM * 100)];                // Einheit 0,01 kg/m
    }
}

public readonly record struct ControlResponse(byte RequestOpCode, byte Result)
{
    public bool Success => Result == 0x01;

    public static ControlResponse? TryParse(byte[] d) =>
        d.Length >= 3 && d[0] == 0x80 ? new ControlResponse(d[1], d[2]) : (ControlResponse?)null;
}