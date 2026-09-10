using System.IO;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Core.Modes;
using PriceSentinel3000.Infrastructure.Storage;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Fact]
    public Task LiveAcknowledgement_IsSavedImmediatelyAndRestoredWithoutArming() => host.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), $"pricesentinel-live-warning-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        try
        {
            await using (var workspace = new TestWorkspace(preferencesStore: new JsonUserPreferencesStore(path)))
            {
                MainViewModel vm = workspace.ViewModel;
                Assert.False(vm.LiveRiskAcknowledged);
                Assert.True(vm.RequestModeSelection(TradingMode.Live));
                Assert.False(vm.LiveRiskAcknowledged);
                workspace.Broker.HoldConnection = true;

                Task acknowledgement = vm.AcknowledgeLiveRiskAsync();
                Assert.False(acknowledgement.IsCompleted);
                Assert.True(new JsonUserPreferencesStore(path).Load()!.LiveRiskAcknowledged);
                Assert.False(vm.LiveArmed);
                await vm.StopSessionCommand.ExecuteAsync();
                await acknowledgement;
            }

            await using (var restarted = new TestWorkspace(preferencesStore: new JsonUserPreferencesStore(path)))
            {
                MainViewModel vm = restarted.ViewModel;
                Assert.True(vm.LiveRiskAcknowledged);
                Assert.Equal(TradingMode.Off, vm.SelectedMode);
                Assert.Equal(TradingMode.Off, vm.EffectiveMode);
                Assert.False(vm.LiveArmed);
                Assert.False(vm.IsSessionRunning);
                Assert.Equal(0, restarted.Broker.Connections);

                vm.Symbol = "AAPL";
                Assert.True(new JsonUserPreferencesStore(path).Load()!.LiveRiskAcknowledged);
                Assert.True(vm.RequestModeSelection(TradingMode.Live));
                await vm.AcknowledgeLiveRiskAsync();
                Assert.Equal(TradingMode.Live, vm.EffectiveMode);
                Assert.False(vm.LiveArmed);
                Assert.False(vm.IsSessionRunning);
            }
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
            Directory.Delete(directory);
        }
    });

    [Fact]
    public Task CancelledLiveWarning_RemainsUnacknowledgedAfterRestart() => host.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), $"pricesentinel-live-warning-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        try
        {
            await using (var workspace = new TestWorkspace(preferencesStore: new JsonUserPreferencesStore(path)))
            {
                MainViewModel vm = workspace.ViewModel;
                Assert.True(vm.RequestModeSelection(TradingMode.Live));
                vm.CancelModeSelection();
                vm.SavePreferences();
                Assert.False(new JsonUserPreferencesStore(path).Load()!.LiveRiskAcknowledged);
                Assert.Equal(TradingMode.Off, vm.EffectiveMode);
            }

            await using var restarted = new TestWorkspace(preferencesStore: new JsonUserPreferencesStore(path));
            Assert.False(restarted.ViewModel.LiveRiskAcknowledged);
            Assert.False(restarted.ViewModel.LiveArmed);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
            Directory.Delete(directory);
        }
    });
}
