using System.Buffers.Binary;

namespace Domestique.Core.Protocol;

public sealed record IndoorBikeData(double? SpeedKmh, double? CadenceRpm, int? PowerW, int? HeartRateBpm);

// Fixed field sizes per FTMS 1.0, as in Zwift-compatible apps (qdomyos-zwift, GoldenCheetah, pycycling, Auuki).
public static class IndoorBikeDataParser
{
    public static IndoorBikeData? Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < 2) return null;
        ushort f = BinaryPrimitives.ReadUInt16LittleEndian(d);
        if (d.Length < Length(f)) return null;         // too short: don't guess
        return Read(d, f);                             // trailing vendor bytes are ignored (Van Rysel D100)
    }

    private static int Length(ushort f)
    {
        int n = 2;                                     // flags
        if ((f & 0x0001) == 0) n += 2;                 // bit 0 clear: speed present
        if ((f & 0x0002) != 0) n += 2;                 // average speed
        if ((f & 0x0004) != 0) n += 2;                 // cadence
        if ((f & 0x0008) != 0) n += 2;                 // average cadence
        if ((f & 0x0010) != 0) n += 3;                 // total distance
        if ((f & 0x0020) != 0) n += 2;                 // resistance level (sint16)
        if ((f & 0x0040) != 0) n += 2;                 // power
        if ((f & 0x0080) != 0) n += 2;                 // average power
        if ((f & 0x0100) != 0) n += 5;                 // energy
        if ((f & 0x0200) != 0) n += 1;                 // heart rate
        if ((f & 0x0400) != 0) n += 1;                 // MET
        if ((f & 0x0800) != 0) n += 2;                 // elapsed time
        if ((f & 0x1000) != 0) n += 2;                 // remaining time
        return n;
    }

    private static IndoorBikeData Read(ReadOnlySpan<byte> d, ushort f)
    {
        int i = 2;
        double? speed = null, cadence = null;
        int? power = null, heartRate = null;
        if ((f & 0x0001) == 0) { speed = BinaryPrimitives.ReadUInt16LittleEndian(d[i..]) / 100.0; i += 2; }
        if ((f & 0x0002) != 0) i += 2;
        if ((f & 0x0004) != 0) { cadence = BinaryPrimitives.ReadUInt16LittleEndian(d[i..]) / 2.0; i += 2; }
        if ((f & 0x0008) != 0) i += 2;
        if ((f & 0x0010) != 0) i += 3;
        if ((f & 0x0020) != 0) i += 2;
        if ((f & 0x0040) != 0) { power = BinaryPrimitives.ReadInt16LittleEndian(d[i..]); i += 2; }
        if ((f & 0x0080) != 0) i += 2;
        if ((f & 0x0100) != 0) i += 5;
        if ((f & 0x0200) != 0) heartRate = d[i];
        return new IndoorBikeData(speed, cadence, power, heartRate);
    }
}
