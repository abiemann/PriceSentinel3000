using PriceSentinel3000.Core.Configuration;
using PriceSentinel3000.Infrastructure.Storage;

namespace PriceSentinel3000.Core.Tests.Storage;

public sealed class JsonUserPreferencesStoreTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsEveryEditableSetting()
    {
        string directory = CreateTestDirectory();
        string path = Path.Combine(directory, "preferences.json");
        var store = new JsonUserPreferencesStore(path);
        PaperTraderSettings expected = PaperTraderSettings.Default with
        {
            Symbol = "USO",
            StartingBalance = 12_345.67m,
            PositionSizeBasis = AmountBasis.FixedAmount,
            PositionSizeValue = 450m,
            QuantityLimitMode = QuantityLimitMode.MaximumShares,
            MaximumQuantity = 17m,
            UnlimitedEntries = true,
            MaximumEntriesPerDay = 23,
            MaximumDailyLossBasis = AmountBasis.FixedAmount,
            MaximumDailyLossValue = 125m,
            StopLossBasis = StopLossBasis.FixedAmount,
            StopLossValue = 18m,
            BufferMinutes = 9,
            QuotePollingSeconds = 7,
            ReconciliationSeconds = 60,
            ReconciliationOverlapSeconds = 20,
            ReplayDate = "2026-07-31",
            ReplayTime = "13:52",
            ReplayDurationMinutes = 120,
            ReplaySpeed = 25m,
        };

        try
        {
            Assert.True(store.Save(expected));
            Assert.Equal(expected, store.Load());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_WithCorruptJson_ReturnsNull()
    {
        string directory = CreateTestDirectory();
        string path = Path.Combine(directory, "preferences.json");
        var store = new JsonUserPreferencesStore(path);

        try
        {
            File.WriteAllText(path, "{ definitely-not-json }");
            Assert.Null(store.Load());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTestDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "PriceSentinel3000-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
