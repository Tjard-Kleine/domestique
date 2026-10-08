using Domestique.Core;
using Xunit;

namespace Domestique.Tests;

public class PowerZonesTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 1)]                                  // 40 %
    [InlineData(137, 1)]                                  // 54,8 %
    [InlineData(138, 2)]                                  // 55,2 %
    [InlineData(190, 3)]                                  // 76 %
    [InlineData(250, 4)]                                  // 100 %
    [InlineData(265, 5)]                                  // 106 %
    [InlineData(303, 6)]                                  // 121,2 %
    [InlineData(600, 6)]
    public void Zone_at_250_watt_ftp(int watts, int zone) => Assert.Equal(zone, PowerZones.Of(watts, 250));

    [Fact]
    public void Without_ftp_there_is_no_zone() => Assert.Equal(0, PowerZones.Of(200, 0));
}
