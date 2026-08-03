using PriceSentinel3000.Infrastructure.MarketData;

namespace PriceSentinel3000.Core.Tests.MarketData;

public sealed class RobinhoodMcpMarketDataSourceTests
{
    [Fact]
    public void EquityHistoricalBounds_IncludesOvernightTrading()
    {
        Assert.Equal(
            "24_5",
            RobinhoodMcpMarketDataSource.EquityHistoricalBounds);
    }
}
