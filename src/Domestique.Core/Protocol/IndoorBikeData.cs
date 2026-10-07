using System.Buffers.Binary;

namespace Domestique.Core.Protocol;

public sealed record IndoorBikeData(double? SpeedKmh, double? CadenceRpm, int? PowerW, int? HeartRateBpm);

public static class IndoorBikeDataParser
{
    public static IndoorBikeData? Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < 2) return null;
        ushort f = BinaryPrimitives.ReadUInt16LittleEndian(d);
        if (Length(f, resistanceBytes: 2) == d.Length) return Read(d, f, 2);
        if ((f & 0x0020) != 0 && Length(f, resistanceBytes: 1) == d.Length) return Read(d, f, 1);
        if (d.Length > Length(f, resistanceBytes: 2)) return Read(d, f, 2);   // angehängte Herstellerbytes ignorieren (Van Rysel D100)
        return null;                                   // zu kurz: nicht raten
    }

    private static int Length(ushort f, int resistanceBytes)
    {
        int n = 2;                                     // Flags
        if ((f & 0x0001) == 0) n += 2;                 // Bit 0 = 0: Speed vorhanden
        if ((f & 0x0002) != 0) n += 2;                 // Average Speed
        if ((f & 0x0004) != 0) n += 2;                 // Cadence
        if ((f & 0x0008) != 0) n += 2;                 // Average Cadence
        if ((f & 0x0010) != 0) n += 3;                 // Total Distance
        if ((f & 0x0020) != 0) n += resistanceBytes;   // Resistance Level
        if ((f & 0x0040) != 0) n += 2;                 // Power
        if ((f & 0x0080) != 0) n += 2;                 // Average Power
        if ((f & 0x0100) != 0) n += 5;                 // Energie
        if ((f & 0x0200) != 0) n += 1;                 // Heart Rate
        if ((f & 0x0400) != 0) n += 1;                 // MET
        if ((f & 0x0800) != 0) n += 2;                 // Elapsed Time
        if ((f & 0x1000) != 0) n += 2;                 // Remaining Time
        return n;
    }

    private static IndoorBikeData Read(ReadOnlySpan<byte> d, ushort f, int resistanceBytes)
    {
        int i = 2;
        double? speed = null, cadence = null;
        int? power = null, heartRate = null;
        if ((f & 0x0001) == 0) { speed = BinaryPrimitives.ReadUInt16LittleEndian(d[i..]) / 100.0; i += 2; }
        if ((f & 0x0002) != 0) i += 2;
        if ((f & 0x0004) != 0) { cadence = BinaryPrimitives.ReadUInt16LittleEndian(d[i..]) / 2.0; i += 2; }
        if ((f & 0x0008) != 0) i += 2;
        if ((f & 0x0010) != 0) i += 3;
        if ((f & 0x0020) != 0) i += resistanceBytes;
        if ((f & 0x0040) != 0) { power = BinaryPrimitives.ReadInt16LittleEndian(d[i..]); i += 2; }
        if ((f & 0x0080) != 0) i += 2;
        if ((f & 0x0100) != 0) i += 5;
        if ((f & 0x0200) != 0) heartRate = d[i];
        return new IndoorBikeData(speed, cadence, power, heartRate);
    }
}