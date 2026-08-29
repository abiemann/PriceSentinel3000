using PriceSentinel3000.Infrastructure.MarketData;

namespace PriceSentinel3000.Infrastructure.Tests.MarketData;

public sealed class RobinhoodMcpGatewayTests
{
    [Fact]
    public void EquityHistoricalBounds_IncludesOvernightTrading()
    {
        Assert.Equal(
            "24_5",
            RobinhoodMcpGateway.EquityHistoricalBounds);
    }
}
