using Servitium.Server.Hmcl;

using Xunit;

namespace Servitium.Server.IntegrationTests;

public sealed class HmclProtocolUnitTests
{
    [Theory]
    [InlineData("bytes=2-5", 10, 2, 4, true)]
    [InlineData("bytes=7-", 10, 7, 3, true)]
    [InlineData("bytes=-4", 10, 6, 4, true)]
    [InlineData("bytes=10-", 10, 0, 0, false)]
    [InlineData("bytes=5-2", 10, 0, 0, false)]
    [InlineData("bytes=-0", 10, 0, 0, false)]
    [Trait("Category", "Unit")]
    [Trait("TestId", "UNT-550")]
    public void Range解析_覆盖闭区间开放区间后缀与越界(
        string header,
        long totalLength,
        long expectedStart,
        long expectedLength,
        bool expectedValid)
    {
        HmclDistributionEndpoints.ByteRange range = Assert.IsType<HmclDistributionEndpoints.ByteRange>(
            HmclDistributionEndpoints.ParseRange(header, totalLength));

        Assert.Equal(expectedStart, range.Start);
        Assert.Equal(expectedLength, range.Length);
        Assert.Equal(expectedValid, range.Valid);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("TestId", "UNT-550")]
    public void Range解析_多区间按全量响应处理()
    {
        Assert.Null(HmclDistributionEndpoints.ParseRange("bytes=0-1,3-4", 10));
    }
}
