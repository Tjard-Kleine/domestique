using Domestique.Core.Protocol;

namespace Domestique.Tests;

public class HeartRateParserTests
{
    [Theory]
    [InlineData(new byte[] { 0x00, 0x48 }, 72)]
    [InlineData(new byte[] { 0x01, 0x2C, 0x01 }, 300)]
    [InlineData(new byte[] { 0x16, 0x8C, 0x10, 0x03 }, 140)]           // with contact bits and RR interval
    public void Reads_heart_rate(byte[] packet, int expected) =>
        Assert.Equal(expected, HeartRateParser.Parse(packet));

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x00 })]
    [InlineData(new byte[] { 0x01, 0x2C })]                               // two bytes announced, one present
    public void Too_short_returns_null(byte[] packet) =>
        Assert.Null(HeartRateParser.Parse(packet));
}
