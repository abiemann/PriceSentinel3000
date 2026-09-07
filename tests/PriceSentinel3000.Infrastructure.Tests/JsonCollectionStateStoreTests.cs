using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Infrastructure.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.Tests;

public sealed class JsonCollectionStateStoreTests
{
    [Fact]
    public void RoundTripPersistsPrivateSettingsAndQueueWithoutWritingInsideLibrary()
    {
        string root = Path.Combine(Path.GetTempPath(), $"collection-config-{Guid.NewGuid():N}");
        string path = Path.Combine(root, "private", "collection.json");
        string library = Path.Combine(root, "candles");
        try
        {
            var store = new JsonCollectionStateStore(path);
            var state = new CollectionState { Settings = new() { LibraryRootPath = library,
                TimeZoneId = "America/Los_Angeles", Lists = [new(Guid.NewGuid(), "My list", true, [new("SOFI")], "private-list")] },
                Jobs = [new() { Symbol = "SOFI", LibraryRootPath = library, SessionDate = new(2026, 9, 4), Status = CollectionJobStatus.Partial }],
                LastScheduledOccurrenceUtc = DateTimeOffset.Parse("2026-09-04T20:15:00Z") };
            store.Save(state);
            CollectionState copy = new JsonCollectionStateStore(path).Load();
            Assert.Equal("private-list", copy.Settings.Lists[0].SourceListId);
            Assert.Equal(CollectionJobStatus.Partial, Assert.Single(copy.Jobs).Status);
            Assert.Equal(state.LastScheduledOccurrenceUtc, copy.LastScheduledOccurrenceUtc);
            Assert.False(Directory.Exists(library));
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
            Assert.Throws<ArgumentException>(() => store.Save(state with { Settings = state.Settings with { LibraryRootPath = "relative" } }));
            Assert.Equal("private-list", store.Load().Settings.Lists[0].SourceListId);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void InvalidExistingStateIsReportedWithoutResettingIt()
    {
        string path = Path.Combine(Path.GetTempPath(), $"collection-invalid-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"SchemaVersion\":2}");
            Assert.Throws<InvalidDataException>(() => new JsonCollectionStateStore(path).Load());
            Assert.Equal("{\"SchemaVersion\":2}", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
}
