using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Fact]
    public Task ListEditor_NewDraftShowsSaveAndSavedEditsShowUpdateUntilReverted() => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        DataRetentionViewModel vm = fixture.ViewModel;
        Assert.False(vm.HasSavedLists);
        Assert.True(vm.ShowSaveListButton);
        Assert.Equal("SAVE LIST", vm.SaveListButtonText);
        Assert.True(vm.SaveListCommand.CanExecute(null));
        vm.TickerInput = "NFLX SOXL";
        await vm.AddTickersAsync();
        await vm.SaveListCommand.ExecuteAsync();
        Assert.True(vm.HasSavedLists);
        Assert.False(vm.ShowSaveListButton);
        Assert.False(vm.SaveListCommand.CanExecute(null));

        var visibility = new List<bool>();
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.ShowSaveListButton)) visibility.Add(vm.ShowSaveListButton);
        };
        vm.Members[0].IsIncluded = false;
        Assert.True(vm.ShowSaveListButton);
        Assert.Equal("UPDATE LIST", vm.SaveListButtonText);
        Assert.True(vm.SaveListCommand.CanExecute(null));
        Assert.True(vm.SelectedList!.Members[0].IsIncluded);
        vm.Members[0].IsIncluded = true;
        Assert.False(vm.ShowSaveListButton);
        Assert.False(vm.SaveListCommand.CanExecute(null));
        Assert.Equal(new[] { true, false }, visibility);

        vm.Members[0].IsIncluded = false;
        await vm.SaveListCommand.ExecuteAsync();
        Assert.False(vm.ShowSaveListButton);
        Assert.False(vm.SelectedList!.Members[0].IsIncluded);
        Assert.Equal(new[] { true, false, true, false }, visibility);
    });

    [Fact]
    public Task ListEditor_TracksListNameEnabledAndMembershipButNotUnaddedTickerInput() => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        await fixture.SaveSingleSymbol(queueDate: false);
        DataRetentionViewModel vm = fixture.ViewModel;
        string savedName = vm.ListName;
        vm.ListName = "Renamed list";
        Assert.True(vm.ShowSaveListButton);
        vm.ListName = savedName;
        Assert.False(vm.ShowSaveListButton);
        vm.ListName = " " + savedName + " ";
        Assert.False(vm.ShowSaveListButton);
        vm.ListEnabled = false;
        Assert.True(vm.ShowSaveListButton);
        vm.ListEnabled = true;
        Assert.False(vm.ShowSaveListButton);
        vm.TickerInput = "NVDA";
        Assert.False(vm.ShowSaveListButton);
        await vm.AddTickersAsync();
        Assert.True(vm.ShowSaveListButton);
        vm.Members.RemoveAt(1);
        Assert.False(vm.ShowSaveListButton);
        DownloadMemberViewModel savedMember = vm.Members[0];
        vm.Members.Clear();
        Assert.True(vm.ShowSaveListButton);
        vm.Members.Add(savedMember);
        Assert.False(vm.ShowSaveListButton);
    });

    [Fact]
    public Task ListEditor_SelectionLoadsAndCollectionResetDoNotLeaveFalseEditsOrStaleMemberListeners() => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        await fixture.SaveSingleSymbol(queueDate: false);
        DataRetentionViewModel vm = fixture.ViewModel;
        DownloadList saved = vm.SelectedList!;
        DownloadMemberViewModel previousMember = vm.Members[0];
        vm.NewList();
        Assert.True(vm.ShowSaveListButton);
        Assert.Equal("SAVE LIST", vm.SaveListButtonText);
        int commandNotifications = 0;
        vm.SaveListCommand.CanExecuteChanged += (_, _) => commandNotifications++;
        previousMember.IsIncluded = false;
        Assert.Equal(0, commandNotifications);
        vm.SelectedList = saved;
        Assert.False(vm.ShowSaveListButton);
        var visibility = new List<bool>();
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.ShowSaveListButton)) visibility.Add(vm.ShowSaveListButton);
        };
        vm.SelectedList = saved;
        Assert.Empty(visibility);
        vm.Members[0].IsIncluded = false;
        Assert.Equal(new[] { true }, visibility);
        vm.SelectedList = saved;
        Assert.Equal(new[] { true, false }, visibility);
        Assert.True(vm.Members[0].IsIncluded);

        await using DataRetentionViewModel reopened = CreateScanViewModel(fixture, new ScanLibrary(fixture.LibraryRoot));
        Assert.True(reopened.HasSavedLists);
        Assert.False(reopened.ShowSaveListButton);
        Assert.Equal("UPDATE LIST", reopened.SaveListButtonText);
    });

    [Fact]
    public Task ListEditor_WatchlistRefreshOnlyOffersUpdateForChangedSavedContent() => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        DataRetentionViewModel vm = fixture.ViewModel;
        fixture.Provider.Members = [new("instrument", "NFLX", "id-nflx"), new("instrument", "SOXL", "id-soxl")];
        await vm.LoadWatchlistsAsync();
        await vm.PreviewWatchlistAsync(false);
        Assert.Equal("SAVE LIST", vm.SaveListButtonText);
        Assert.True(vm.ShowSaveListButton);
        vm.Members[1].IsIncluded = false;
        await vm.SaveListCommand.ExecuteAsync();
        var visibility = new List<bool>();
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.ShowSaveListButton)) visibility.Add(vm.ShowSaveListButton);
        };
        await vm.PreviewWatchlistAsync(true);
        Assert.False(vm.ShowSaveListButton);
        Assert.False(vm.Members[1].IsIncluded);
        Assert.Empty(visibility);
        fixture.Provider.Members = [new("instrument", "SOXL", "id-soxl"), new("instrument", "NVDA", "id-nvda")];
        await vm.PreviewWatchlistAsync(true);
        Assert.True(vm.ShowSaveListButton);
        Assert.Equal("UPDATE LIST", vm.SaveListButtonText);
        Assert.False(vm.Members[0].IsIncluded);
        Assert.Equal(new[] { true }, visibility);
        await vm.SaveListCommand.ExecuteAsync();
        Assert.False(vm.ShowSaveListButton);
        Assert.Equal(new[] { true, false }, visibility);
    });

    [Fact]
    public Task ListEditor_WatchlistSourceChangeIsSavableWhenTheEquitiesAreUnchanged() => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        fixture.Connected = true;
        DataRetentionViewModel vm = fixture.ViewModel;
        vm.TickerInput = "NFLX";
        await vm.AddTickersAsync();
        await vm.SaveListCommand.ExecuteAsync();
        DownloadList saved = vm.SelectedList!;
        Assert.Null(saved.SourceListId);
        fixture.Provider.Members = [new("instrument", "NFLX", "id-nflx")];
        await vm.LoadWatchlistsAsync();
        await vm.PreviewWatchlistAsync(true);
        Assert.Equal(saved.Members, vm.Members.Select(member => member.ToMember()));
        Assert.True(vm.ShowSaveListButton);
        Assert.Equal("UPDATE LIST", vm.SaveListButtonText);
        await vm.SaveListCommand.ExecuteAsync();
        Assert.Equal("private-watchlist-id", vm.SelectedList!.SourceListId);
        Assert.False(vm.ShowSaveListButton);
    });

    [Fact]
    public Task ListEditor_ImportAndDeletePublishSavedListAvailabilityAndLeaveCorrectSaveState() => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        DataRetentionViewModel vm = fixture.ViewModel;
        var available = new List<bool>();
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.HasSavedLists)) available.Add(vm.HasSavedLists);
        };
        string json = DownloadListTransfer.Export([new(Guid.NewGuid(), "Imported", true, [new("NFLX")])]);
        await vm.ImportListsAsync(json);
        Assert.True(vm.HasSavedLists);
        Assert.False(vm.ShowSaveListButton);
        Assert.Equal("UPDATE LIST", vm.SaveListButtonText);
        Assert.Equal(new[] { true }, available);
        await vm.DeleteListCommand.ExecuteAsync();
        Assert.False(vm.HasSavedLists);
        Assert.True(vm.ShowSaveListButton);
        Assert.Equal("SAVE LIST", vm.SaveListButtonText);
        Assert.Equal(new[] { true, false }, available);
    });
}
