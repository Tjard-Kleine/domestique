namespace Domestique.Core.Protocol;
public static class BleUuids
{
    public static Guid FromShort(ushort id) => Guid.Parse($"0000{id:X4}-0000-1000-8000-00805F9B34FB");

    public static readonly Guid FitnessMachineService = FromShort(0x1826);
    public static readonly Guid IndoorBikeData        = FromShort(0x2AD2);
    public static readonly Guid SupportedPowerRange   = FromShort(0x2AD8);
    public static readonly Guid ControlPoint          = FromShort(0x2AD9);
    public static readonly Guid MachineStatus         = FromShort(0x2ADA);
    public static readonly Guid HeartRateService      = FromShort(0x180D);
    public static readonly Guid HeartRateMeasurement  = FromShort(0x2A37);
}