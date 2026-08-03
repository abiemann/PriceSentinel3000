using PriceSentinel3000.Core.Configuration;
using PriceSentinel3000.Infrastructure.Storage;

namespace PriceSentinel3000.Core.Tests.Storage;

public sealed class JsonUserPreferencesStoreTests
{
    [Fact]
    public void UpdatedChoiceEnums_PreserveSavedNumericValues()
    {
        Assert.Equal(0, (int)QuantityLimitMode.AsManyAsPossible);
        Assert.Equal(1, (int)QuantityLimitMode.NoMoreThan);
        Assert.Equal(0, (int)StopLossBasis.TotalPositionLossAmount);
        Assert.Equal(1, (int)StopLossBasis.PurchasePriceDeclinePercentage);
    }

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
            TradesSettleImmediately = false,
            PositionSizeBasis = AmountBasis.FixedAmount,
            PositionSizeValue = 450m,
            QuantityLimitMode = QuantityLimitMode.NoMoreThan,
            MaximumQuantity = 17m,
            UnlimitedEntries = true,
            MaximumEntriesPerDay = 23,
            MaximumDailyLossBasis = AmountBasis.FixedAmount,
            MaximumDailyLossValue = 125m,
            StopLossBasis = StopLossBasis.TotalPositionLossAmount,
            StopLossValue = 18m,
            BufferMinutes = 9,
            QuotePollingSeconds = 7,
            ChartCandleIntervalSeconds = 120,
            ReconciliationSeconds = 60,
            ReconciliationLookbackSeconds = 600,
            ReconciliationCompletionDelaySeconds = 45,
            ReplayDate = "2026-07-31",
            ReplayTime = "13:52",
            ReplayEndTime = "15:52",
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
    public void Load_LegacyReplayDurationMigratesToLocalEndTime()
    {
        string directory = CreateTestDirectory();
        string path = Path.Combine(directory, "preferences.json");
        var store = new JsonUserPreferencesStore(path);

        try
        {
            File.WriteAllText(
                path,
                """
                {
                  "symbol": "USO",
                  "replayTime": "13:52",
                  "replayDurationMinutes": 120
                }
                """);

            PaperTraderSettings loaded = Assert.IsType<PaperTraderSettings>(store.Load());

            Assert.Equal("15:52", loaded.ReplayEndTime);
            Assert.Equal(300, loaded.ReconciliationLookbackSeconds);
            Assert.Equal(30, loaded.ReconciliationCompletionDelaySeconds);
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
