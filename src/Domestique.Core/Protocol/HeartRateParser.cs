namespace Domestique.Core.Protocol;

// Heart Rate Measurement (0x2A37): Bit 0 im ersten Byte sagt, ob der Puls ein oder zwei Bytes lang ist.
public static class HeartRateParser
{
    public static int? Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < 2) return null;
        if ((d[0] & 0x01) == 0) return d[1];          // 1 Byte
        if (d.Length < 3) return null;
        return d[1] | d[2] << 8;                      // 2 Byte
    }
}
