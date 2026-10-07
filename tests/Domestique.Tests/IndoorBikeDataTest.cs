using Domestique.Core.Protocol;
using Xunit;

namespace Domestique.Tests;

public class IndoorBikeDataTest
{
    [Fact]
    public void Read_speed_cadence_power()
    {
        byte[] packet = [0x44, 0x00, 0xA4, 0x0B, 0xB4, 0x00, 0xC8, 0x00];
        var data = IndoorBikeDataParser.Parse(packet);
        Assert.NotNull(data);
        Assert.Equal(29.80, data.SpeedKmh);
        Assert.Equal(90.0, data.CadenceRpm);
        Assert.Equal(200, data.PowerW);
        Assert.Null(data.HeartRateBpm);
    }

    [Fact]
    public void Ignores_appended_vendor_bytes()                  // echtes Paket vom Van Rysel D100
    {
        var d = IndoorBikeDataParser.Parse(Convert.FromHexString("40008C020900E101000000001C00E803"));
        Assert.NotNull(d);
        Assert.Equal(6.52, d.SpeedKmh!.Value, 2);
        Assert.Equal(9, d.PowerW);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x44 })]
    public void Too_short_returns_null(byte[] packet)
    {
        Assert.Null(IndoorBikeDataParser.Parse(packet));
    }

    [Fact]
    public void Flags_only_with_more_data_bit_has_no_values()
    {
        byte[] packet = [0x01, 0x00];
        var data = IndoorBikeDataParser.Parse(packet);
        Assert.NotNull(data);
        Assert.Null(data.SpeedKmh);
        Assert.Null(data.CadenceRpm);
        Assert.Null(data.PowerW);
        Assert.Null(data.HeartRateBpm);
    }

    [Fact]
    public void Missing_speed_returns_null()
    {
        byte[] packet = [0x00, 0x00];
        Assert.Null(IndoorBikeDataParser.Parse(packet));
    }

    [Fact]
    public void More_data_bit_means_no_speed()
    {
        byte[] packet = [0x45, 0x00, 0xB4, 0x00, 0xC8, 0x00];
        var data = IndoorBikeDataParser.Parse(packet);
        Assert.NotNull(data);
        Assert.Null(data.SpeedKmh);
        Assert.Equal(90.0, data.CadenceRpm);
        Assert.Equal(200, data.PowerW);
    }

    [Theory]
    [InlineData(0x01, 0x00, 0.01)]
    [InlineData(0x94, 0x0B, 29.64)]
    [InlineData(0xFF, 0xFF, 655.35)]
    public void Speed_has_resolution_of_one_hundredth(byte lo, byte hi, double expected)
    {
        byte[] packet = [0x00, 0x00, lo, hi];
        Assert.Equal(expected, IndoorBikeDataParser.Parse(packet)?.SpeedKmh);
    }

    [Theory]
    [InlineData(0x00, 0x00, 0.0)]
    [InlineData(0xB5, 0x00, 90.5)]
    [InlineData(0xFF, 0xFF, 32767.5)]
    public void Cadence_has_resolution_of_half_rpm(byte lo, byte hi, double expected)
    {
        byte[] packet = [0x05, 0x00, lo, hi];
        Assert.Equal(expected, IndoorBikeDataParser.Parse(packet)?.CadenceRpm);
    }

    [Theory]
    [InlineData(0xC8, 0x00, 200)]
    [InlineData(0x9C, 0xFF, -100)]
    [InlineData(0xFF, 0x7F, 32767)]
    [InlineData(0x00, 0x80, -32768)]
    public void Power_is_signed(byte lo, byte hi, int expected)
    {
        byte[] packet = [0x41, 0x00, lo, hi];
        Assert.Equal(expected, IndoorBikeDataParser.Parse(packet)?.PowerW);
    }

    [Theory]
    [InlineData(0x8C, 140)]
    [InlineData(0xFF, 255)]
    public void Read_heart_rate(byte raw, int expected)
    {
        byte[] packet = [0x01, 0x02, raw];
        Assert.Equal(expected, IndoorBikeDataParser.Parse(packet)?.HeartRateBpm);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void All_fields_are_skipped_at_correct_offsets(int trailingBytes)
    {
        byte[] packet = [.. AllFieldsPacket(resistanceBytes: 2), .. new byte[trailingBytes]];
        var data = IndoorBikeDataParser.Parse(packet);
        Assert.NotNull(data);
        Assert.Equal(29.64, data.SpeedKmh);
        Assert.Equal(90.0, data.CadenceRpm);
        Assert.Equal(200, data.PowerW);
        Assert.Equal(140, data.HeartRateBpm);
    }

    // Resistance Level ist wie bei Zwift & Co. immer sint16; kürzer heißt: Paket zu kurz
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void All_fields_with_short_resistance_returns_null(int resistanceBytes)
    {
        Assert.Null(IndoorBikeDataParser.Parse(AllFieldsPacket(resistanceBytes)));
    }

    [Fact]
    public void Truncated_packet_returns_null()
    {
        byte[] packet = [0x44, 0x00, 0x94, 0x0B, 0xB4, 0x00, 0xC8];
        Assert.Null(IndoorBikeDataParser.Parse(packet));
    }

    [Theory]
    [InlineData(new byte[] { 0x00 })]
    [InlineData(new byte[] { 0xAB, 0xCD, 0xEF })]
    public void Trailing_vendor_bytes_are_ignored(byte[] trailing)
    {
        byte[] packet = [0x44, 0x00, 0x94, 0x0B, 0xB4, 0x00, 0xC8, 0x00, .. trailing];
        var data = IndoorBikeDataParser.Parse(packet);
        Assert.NotNull(data);
        Assert.Equal(29.64, data.SpeedKmh);
        Assert.Equal(90.0, data.CadenceRpm);
        Assert.Equal(200, data.PowerW);
    }

    [Fact]
    public void Reserved_flag_bits_are_ignored()
    {
        byte[] packet = [0x44, 0xE0, 0x94, 0x0B, 0xB4, 0x00, 0xC8, 0x00];
        var data = IndoorBikeDataParser.Parse(packet);
        Assert.NotNull(data);
        Assert.Equal(29.64, data.SpeedKmh);
        Assert.Equal(90.0, data.CadenceRpm);
        Assert.Equal(200, data.PowerW);
    }

    // Alle Felder gesetzt (außer More Data); Füllbytes sind unterschiedlich, damit Offset-Fehler auffallen
    private static byte[] AllFieldsPacket(int resistanceBytes) =>
    [
        0xFE, 0x1F,                                     // Flags
        0x94, 0x0B,                                     // Speed 29.64 km/h
        0x11, 0x11,                                     // Average Speed
        0xB4, 0x00,                                     // Cadence 90 rpm
        0x22, 0x22,                                     // Average Cadence
        0x33, 0x33, 0x33,                               // Total Distance
        .. Enumerable.Repeat((byte)0x44, resistanceBytes), // Resistance Level
        0xC8, 0x00,                                     // Power 200 W
        0x55, 0x55,                                     // Average Power
        0x66, 0x66, 0x66, 0x66, 0x66,                   // Energie
        0x8C,                                           // Heart Rate 140 bpm
        0x77,                                           // MET
        0x88, 0x88,                                     // Elapsed Time
        0x99, 0x99,                                     // Remaining Time
    ];
}
