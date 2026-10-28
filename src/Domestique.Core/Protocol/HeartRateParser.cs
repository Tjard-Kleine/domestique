namespace Domestique.Core.Protocol;

// Heart Rate Measurement (0x2A37): bit 0 of the flags says whether the value has one or two bytes.
public static class HeartRateParser
{
    public static int? Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < 2) return null;
        if ((d[0] & 0x01) == 0) return d[1];
        if (d.Length < 3) return null;
        return d[1] | d[2] << 8;
    }
}
