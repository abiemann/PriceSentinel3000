using Microsoft.Data.Sqlite;
using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Infrastructure.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.Tests.MarketDataLibrary;

public sealed class SqliteCollectionGapIndexTests
{
    private static readonly DateTimeOffset From = new(2026, 9, 9, 13, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CheckedAt = From.AddHours(7);
    private static readonly CollectionGapKey Key = new("AAPL", "aapl-instrument", new DateOnly(2026, 9, 9), "24_5", "split", "unversioned");

    [Fact]
    public void MissingDatabaseStartsEmptyAndPersistsObservationsAcrossInstances()
    {
        using var fixture = new Fixture();
        fixture.Index.Initialize();
        Assert.True(File.Exists(fixture.DatabasePath));
        CollectionGapSnapshot initial = fixture.Query();
        Assert.Empty(initial.UnavailableRanges);
        Assert.False(initial.HasReturnedCandles);

        fixture.Index.RecordAttempt(Key, From, From.AddHours(1), [Gap(0, 30)], true, CheckedAt, null);
        var reopened = new SqliteCollectionGapIndex(fixture.DatabasePath);
        CollectionGapSnapshot snapshot = reopened.Query(Key, From.AddMinutes(10), From.AddMinutes(45), CheckedAt);
        Assert.Equal([Gap(10, 30)], snapshot.UnavailableRanges);
        Assert.True(snapshot.HasReturnedCandles);
    }

    [Fact]
    public void AdjacentAndOverlappingRangesWithTheSameExpiryCoalesceInStorage()
    {
        using var fixture = new Fixture();
        fixture.Index.RecordAttempt(Key, From, From.AddMinutes(30), [Gap(15, 30), Gap(0, 20)], false, CheckedAt, null);
        fixture.Index.RecordAttempt(Key, From.AddMinutes(30), From.AddHours(1), [Gap(30, 60)], false, CheckedAt.AddSeconds(1), null);
        Assert.Equal([Gap(0, 60)], fixture.Query().UnavailableRanges);
        Assert.Equal(1, fixture.StoredBlockCount());
    }

    [Fact]
    public void SuccessfulFillClearsOnlyTheCheckedSubsetAndPreservesBothOutsidePieces()
    {
        using var fixture = new Fixture();
        fixture.Index.RecordAttempt(Key, From, From.AddHours(1), [Gap(0, 60)], false, CheckedAt, null);
        fixture.Index.RecordAttempt(Key, From.AddMinutes(15), From.AddMinutes(45), [], true, CheckedAt.AddMinutes(1), null);
        CollectionGapSnapshot snapshot = fixture.Query();
        Assert.Equal([Gap(0, 15), Gap(45, 60)], snapshot.UnavailableRanges);
        Assert.True(snapshot.HasReturnedCandles);
        Assert.Equal(2, fixture.StoredBlockCount());
    }

    [Fact]
    public void NewObservationReplacesOldExpiryInsideItsRangeWithoutChangingOutsideExpiry()
    {
        using var fixture = new Fixture();
        fixture.Index.RecordAttempt(Key, From, From.AddHours(1), [Gap(0, 60)], false, CheckedAt, null);
        DateTimeOffset retriedAt = CheckedAt.AddMinutes(1);
        DateTimeOffset expiry = retriedAt.AddMinutes(15);
        fixture.Index.RecordAttempt(Key, From.AddMinutes(15), From.AddMinutes(45), [Gap(20, 40)], true, retriedAt, expiry);
        Assert.Equal([Gap(0, 15), Gap(20, 40), Gap(45, 60)], fixture.Query(retriedAt).UnavailableRanges);
        Assert.Equal([Gap(0, 15), Gap(45, 60)], fixture.Query(expiry).UnavailableRanges);
    }

    [Fact]
    public void TouchingRangesWithDifferentExpiriesStayDistinctInStorageButQueryAsAUnion()
    {
        using var fixture = new Fixture();
        DateTimeOffset expiry = CheckedAt.AddMinutes(15);
        fixture.Index.RecordAttempt(Key, From, From.AddMinutes(30), [Gap(0, 30)], false, CheckedAt, null);
        fixture.Index.RecordAttempt(Key, From.AddMinutes(30), From.AddHours(1), [Gap(30, 60)], false, CheckedAt, expiry);
        Assert.Equal(2, fixture.StoredBlockCount());
        Assert.Equal([Gap(0, 60)], fixture.Query().UnavailableRanges);
        Assert.Equal([Gap(0, 30)], fixture.Query(expiry).UnavailableRanges);
    }

    [Fact]
    public void TodaysEmptyRangeExpiresAtExactlyFifteenMinutesWithoutLosingPositiveDayEvidence()
    {
        using var fixture = new Fixture();
        DateTimeOffset expiry = CheckedAt.AddMinutes(15);
        fixture.Index.RecordAttempt(Key, From, From.AddHours(1), [Gap(0, 30)], true, CheckedAt, expiry);
        Assert.Single(fixture.Query(expiry.AddTicks(-1)).UnavailableRanges);
        CollectionGapSnapshot expired = fixture.Query(expiry);
        Assert.Empty(expired.UnavailableRanges);
        Assert.True(expired.HasReturnedCandles);
        fixture.Index.RecordAttempt(Key, From.AddHours(1), From.AddHours(2), [], false, expiry, null);
        Assert.Equal(0, fixture.StoredBlockCount());
        Assert.True(fixture.Query(expiry).HasReturnedCandles);
    }

    [Fact]
    public void TodayExpiryDoesNotBecomePermanentWhenTheEasternDateRollsOver()
    {
        using var fixture = new Fixture();
        var lateAttempt = new DateTimeOffset(2026, 9, 10, 3, 55, 0, TimeSpan.Zero);
        fixture.Index.RecordAttempt(Key, From, From.AddHours(1), [Gap(0, 60)], false, lateAttempt, lateAttempt.AddMinutes(15));
        Assert.Single(fixture.Query(lateAttempt.AddMinutes(14)).UnavailableRanges);
        Assert.Empty(fixture.Query(lateAttempt.AddMinutes(15)).UnavailableRanges);
    }

    [Fact]
    public void OlderPermanentObservationRemainsUnavailableAcrossLaterRuns()
    {
        using var fixture = new Fixture();
        fixture.Index.RecordAttempt(Key, From, From.AddHours(1), [Gap(0, 60)], false, CheckedAt, null);
        Assert.Equal([Gap(0, 60)], fixture.Query(CheckedAt.AddYears(1)).UnavailableRanges);
    }

    [Fact]
    public void QueryUsesHalfOpenBoundsAndNormalizesDateTimeOffsets()
    {
        using var fixture = new Fixture();
        fixture.Index.RecordAttempt(Key, From.ToOffset(TimeSpan.FromHours(-7)), From.AddHours(1), [Gap(0, 60)], false, CheckedAt, null);
        Assert.Empty(fixture.Index.Query(Key, From.AddMinutes(-15), From, CheckedAt).UnavailableRanges);
        Assert.Empty(fixture.Index.Query(Key, From.AddHours(1), From.AddHours(2), CheckedAt).UnavailableRanges);
        HistoricalGap gap = Assert.Single(fixture.Index.Query(Key, From.AddMinutes(-1), From.AddMinutes(1), CheckedAt).UnavailableRanges);
        Assert.Equal(Gap(0, 1), gap);
        Assert.Equal(TimeSpan.Zero, gap.FromUtc.Offset);
    }

    [Theory]
    [InlineData("symbol")]
    [InlineData("instrument")]
    [InlineData("unknown-instrument")]
    [InlineData("date")]
    [InlineData("session")]
    [InlineData("policy")]
    [InlineData("basis")]
    [InlineData("interval")]
    public void ObservationsAndPositiveEvidenceAreIsolatedByEveryDayIdentityField(string field)
    {
        using var fixture = new Fixture();
        fixture.Index.RecordAttempt(Key, From, From.AddHours(1), [Gap(0, 30)], true, CheckedAt, null);
        CollectionGapKey other = field switch
        {
            "symbol" => Key with { Symbol = "AMD" },
            "instrument" => Key with { InstrumentId = "new-aapl-instrument" },
            "unknown-instrument" => Key with { InstrumentId = null },
            "date" => Key with { SessionDate = Key.SessionDate.AddDays(-1) },
            "session" => Key with { SessionBounds = "regular" },
            "policy" => Key with { AdjustmentPolicy = "raw" },
            "basis" => Key with { AdjustmentBasis = "different-revision" },
            _ => Key with { SourceIntervalSeconds = 60 }
        };
        CollectionGapSnapshot snapshot = fixture.Index.Query(other, From, From.AddHours(1), CheckedAt);
        Assert.Empty(snapshot.UnavailableRanges);
        Assert.False(snapshot.HasReturnedCandles);
    }

    [Fact]
    public void ProviderAndLibraryDatabasesHaveIndependentObservations()
    {
        using var fixture = new Fixture();
        fixture.Index.RecordAttempt(Key, From, From.AddHours(1), [Gap(0, 30)], true, CheckedAt, null);
        var otherProvider = new SqliteCollectionGapIndex(fixture.DatabasePath, "Other broker");
        CollectionGapSnapshot providerSnapshot = otherProvider.Query(Key, From, From.AddHours(1), CheckedAt);
        Assert.Empty(providerSnapshot.UnavailableRanges);
        Assert.False(providerSnapshot.HasReturnedCandles);
        using var otherLibrary = new Fixture();
        Assert.Empty(otherLibrary.Query().UnavailableRanges);
        Assert.False(otherLibrary.Query().HasReturnedCandles);
    }

    [Fact]
    public void InitializeRebuildsADeletedDatabaseOnTheSameInstanceWithoutRetainedFileHandles()
    {
        using var fixture = new Fixture();
        fixture.Index.RecordAttempt(Key, From, From.AddHours(1), [Gap(0, 30)], true, CheckedAt, null);
        File.Delete(fixture.DatabasePath);
        fixture.Index.Initialize();
        Assert.True(File.Exists(fixture.DatabasePath));
        Assert.Empty(fixture.Query().UnavailableRanges);
        Assert.False(fixture.Query().HasReturnedCandles);
        fixture.Index.RecordAttempt(Key, From.AddMinutes(30), From.AddHours(1), [Gap(30, 60)], false, CheckedAt, null);
        Assert.Equal([Gap(30, 60)], fixture.Query().UnavailableRanges);
    }

    [Theory]
    [InlineData("zero-attempt")]
    [InlineData("reversed-attempt")]
    [InlineData("zero-gap")]
    [InlineData("reversed-gap")]
    [InlineData("gap-before-attempt")]
    [InlineData("gap-after-attempt")]
    public void InvalidRangesAreRejectedBeforeAnyDatabaseMutation(string scenario)
    {
        using var fixture = new Fixture();
        DateTimeOffset through = scenario switch
        {
            "zero-attempt" => From,
            "reversed-attempt" => From.AddMinutes(-1),
            _ => From.AddHours(1)
        };
        HistoricalGap gap = scenario switch
        {
            "zero-gap" => Gap(0, 0),
            "reversed-gap" => Gap(10, 5),
            "gap-before-attempt" => Gap(-1, 10),
            "gap-after-attempt" => Gap(50, 61),
            _ => Gap(0, 30)
        };
        Assert.ThrowsAny<ArgumentException>(() => fixture.Index.RecordAttempt(Key, From, through, [gap], false, CheckedAt, null));
        Assert.False(File.Exists(fixture.DatabasePath));
    }

    [Fact]
    public void InvalidReplacementDoesNotRemoveAnExistingObservation()
    {
        using var fixture = new Fixture();
        fixture.Index.RecordAttempt(Key, From, From.AddHours(1), [Gap(0, 60)], false, CheckedAt, null);
        Assert.Throws<ArgumentException>(() => fixture.Index.RecordAttempt(Key, From.AddMinutes(10), From.AddMinutes(20), [Gap(5, 20)], true, CheckedAt, null));
        Assert.Equal([Gap(0, 60)], fixture.Query().UnavailableRanges);
        Assert.False(fixture.Query().HasReturnedCandles);
    }

    [Fact]
    public void ScopeAndOverlapLookupsUseIndexesInsteadOfScanningTheDatabase()
    {
        using var fixture = new Fixture();
        fixture.Index.RecordAttempt(Key, From, From.AddHours(1), [Gap(0, 30)], false, CheckedAt, null);
        using SqliteConnection connection = fixture.Open();
        string scopePlan = Plan(connection, """
            SELECT scope_id, has_returned_candles FROM collection_gap_scopes
            WHERE provider = 'Robinhood' AND symbol = 'AAPL' AND instrument_id = 'aapl-instrument'
              AND session_date = 1 AND session_bounds = '24_5' AND adjustment_policy = 'split'
              AND adjustment_basis = 'unversioned' AND source_interval_seconds = 15;
            """);
        string rangePlan = Plan(connection, """
            SELECT from_ticks, through_ticks FROM collection_unavailable_ranges
            WHERE scope_id = 1 AND from_ticks < 100 AND through_ticks > 0
              AND (retry_after_ticks IS NULL OR retry_after_ticks > 1) ORDER BY from_ticks;
            """);
        Assert.Contains("SEARCH", scopePlan);
        Assert.DoesNotContain("SCAN ", scopePlan);
        Assert.Contains("SEARCH", rangePlan);
        Assert.Contains("PRIMARY KEY", rangePlan);
        Assert.DoesNotContain("SCAN ", rangePlan);
    }

    private static string Plan(SqliteConnection connection, string query)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + query;
        using SqliteDataReader reader = command.ExecuteReader();
        var details = new List<string>();
        while (reader.Read()) details.Add(reader.GetString(3));
        return string.Join(" ", details).ToUpperInvariant();
    }

    private static HistoricalGap Gap(int fromMinute, int throughMinute) => new(From.AddMinutes(fromMinute), From.AddMinutes(throughMinute));

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "PriceSentinel-gap-index-" + Guid.NewGuid().ToString("N"));
        public Fixture()
        {
            DatabasePath = Path.Combine(_root, "collection-gaps.sqlite");
            Index = new SqliteCollectionGapIndex(DatabasePath);
        }
        public string DatabasePath { get; }
        public SqliteCollectionGapIndex Index { get; }
        public CollectionGapSnapshot Query(DateTimeOffset? now = null) => Index.Query(Key, From, From.AddHours(1), now ?? CheckedAt);
        public SqliteConnection Open()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
            connection.Open();
            return connection;
        }
        public long StoredBlockCount()
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM collection_unavailable_ranges;";
            return (long)command.ExecuteScalar()!;
        }
        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
