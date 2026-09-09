using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PriceSentinel3000.App.Dialogs;
using PriceSentinel3000.App.ViewModels;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Theory]
    [InlineData(1080, 790)]
    [InlineData(860, 620)]
    public Task EquityListButtons_FollowSavedChangesAndFitTheEditor(int width, int height) => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        DataRetentionViewModel vm = fixture.ViewModel;
        var dialog = CreateLocalLayoutDialog(vm, width, height);
        try
        {
            dialog.Show();
            await SettleLocalLayout(dialog);
            var save = (Button)dialog.FindName("SaveListButton");
            var export = (Button)dialog.FindName("ExportListsButton");
            Button import = Assert.Single(FindRetentionVisuals<Button>(dialog), button => Equals(button.Content, "IMPORT LIST FILE"));
            ListBox members = Assert.Single(FindRetentionVisuals<ListBox>(dialog), list => ReferenceEquals(list.ItemsSource, vm.Members));
            Assert.Equal("SAVE LIST", save.Content);
            Assert.True(save.IsVisible);
            Assert.Same(vm.SaveListCommand, save.Command);
            Assert.Equal(Visibility.Collapsed, export.Visibility);
            Assert.True(import.IsVisible);
            await WaitForListButtonReveal(dialog, save);
            AssertListButtonLayout(dialog, members, save, import);

            vm.TickerInput = "AAPL NVDA";
            await vm.AddTickersCommand.ExecuteAsync();
            await vm.SaveListCommand.ExecuteAsync();
            await SettleLocalLayout(dialog);
            Assert.Equal(Visibility.Collapsed, save.Visibility);
            Assert.True(export.IsVisible);
            AssertInsideWindow(dialog, export);
            Guid savedId = Assert.Single(vm.Lists).Id;

            CheckBox inclusion = ListMemberCheckBox(members, "AAPL");
            inclusion.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            await SettleLocalLayout(dialog);
            Assert.False(vm.Members.Single(member => member.Symbol == "AAPL").IsIncluded);
            Assert.True(save.IsVisible);
            Assert.True(save.IsEnabled);
            Assert.Equal("UPDATE LIST", save.Content);
            await WaitForListButtonReveal(dialog, save);
            AssertListButtonLayout(dialog, members, save, import);
            AssertInsideWindow(dialog, export);
            CaptureLocalLayout(dialog, $"equity-lists-{width}x{height}-update.png");

            inclusion.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            await SettleLocalLayout(dialog);
            Assert.Equal(Visibility.Collapsed, save.Visibility);
            Assert.True(export.IsVisible);

            inclusion.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            await SettleLocalLayout(dialog);
            await vm.SaveListCommand.ExecuteAsync();
            await SettleLocalLayout(dialog);
            Assert.Equal(Visibility.Collapsed, save.Visibility);
            Assert.False(Assert.Single(vm.Lists).Members.Single(member => member.Symbol == "AAPL").IsIncluded);
            Assert.Equal(savedId, Assert.Single(vm.Lists).Id);

            vm.NewListCommand.Execute(null);
            await SettleLocalLayout(dialog);
            Assert.True(save.IsVisible);
            Assert.Equal("SAVE LIST", save.Content);
            Assert.True(export.IsVisible); // A new draft does not remove the existing saved list.
            await WaitForListButtonReveal(dialog, save);
            AssertListButtonLayout(dialog, members, save, import);

            vm.SelectedList = vm.Lists.Single();
            await SettleLocalLayout(dialog);
            Assert.Equal(Visibility.Collapsed, save.Visibility);
            await vm.DeleteListCommand.ExecuteAsync();
            await SettleLocalLayout(dialog);
            Assert.True(save.IsVisible);
            Assert.Equal("SAVE LIST", save.Content);
            Assert.Equal(Visibility.Collapsed, export.Visibility);
            Assert.True(import.IsVisible);
            Assert.Equal(0, fixture.Provider.Calls);
            Assert.Equal(0, fixture.ConnectionCalls);
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public Task EquityListButtons_RapidCheckboxRevertDoesNotLeaveTheNextRevealHidden() => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        await fixture.SaveSingleSymbol(queueDate: false);
        DataRetentionViewModel vm = fixture.ViewModel;
        var dialog = CreateLocalLayoutDialog(vm, 860, 620);
        try
        {
            dialog.Show();
            await SettleLocalLayout(dialog);
            var save = (Button)dialog.FindName("SaveListButton");
            ListBox members = Assert.Single(FindRetentionVisuals<ListBox>(dialog), list => ReferenceEquals(list.ItemsSource, vm.Members));
            CheckBox inclusion = ListMemberCheckBox(members, "NFLX");
            Assert.Equal(Visibility.Collapsed, save.Visibility);

            for (int attempt = 0; attempt < 3; attempt++)
            {
                inclusion.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
                await SettleLocalLayout(dialog);
                Assert.True(save.IsVisible);
                inclusion.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                await SettleLocalLayout(dialog);
                Assert.Equal(Visibility.Collapsed, save.Visibility);
            }

            inclusion.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            await SettleLocalLayout(dialog);
            await WaitForListButtonReveal(dialog, save);
            Assert.True(save.IsVisible);
            Assert.True(save.IsEnabled);
            Assert.Equal("UPDATE LIST", save.Content);
            AssertInsideWindow(dialog, save);
            await vm.SaveListCommand.ExecuteAsync();
            await SettleLocalLayout(dialog);
            Assert.Equal(Visibility.Collapsed, save.Visibility);
        }
        finally { dialog.Close(); }
    });

    private static CheckBox ListMemberCheckBox(ListBox members, string symbol) =>
        Assert.Single(FindRetentionVisuals<CheckBox>(members), checkBox =>
            checkBox.DataContext is DownloadMemberViewModel member && member.Symbol == symbol);

    private static async Task WaitForListButtonReveal(Window dialog, Button button)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (button.Opacity < 0.999 || button.RenderTransform.Transform(new Point()).Y > 0.01)
            await Task.Delay(10, timeout.Token);
        await SettleLocalLayout(dialog);
        Assert.Equal(1d, button.Opacity, 3);
    }

    private static void AssertListButtonLayout(DataRetentionDialog dialog, ListBox members, Button save, Button import)
    {
        AssertInsideWindow(dialog, members);
        AssertInsideWindow(dialog, save);
        AssertInsideWindow(dialog, import);
        double membersBottom = members.TranslatePoint(new Point(), dialog).Y + members.ActualHeight;
        Point saveOrigin = save.TranslatePoint(new Point(), dialog);
        double importTop = import.TranslatePoint(new Point(), dialog).Y;
        Assert.True(membersBottom <= saveOrigin.Y);
        Assert.True(saveOrigin.Y + save.ActualHeight <= importTop);
    }
}
