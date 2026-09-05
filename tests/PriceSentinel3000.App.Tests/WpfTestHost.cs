using System.Windows;
using System.Windows.Threading;

namespace PriceSentinel3000.App.Tests;

public sealed class WpfTestHost : IDisposable
{
    private readonly TaskCompletionSource<Dispatcher> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;

    public WpfTestHost()
    {
        _thread = new Thread(() =>
        {
            try
            {
                var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                foreach (string resource in new[] { "Themes/Colors", "Styles/Containers", "Styles/Inputs", "Styles/ScrollBars", "Styles/Buttons" })
                {
                    application.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri($"pack://application:,,,/PriceSentinel3000;component/{resource}.xaml"),
                    });
                }

                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                _ready.SetResult(dispatcher);
                Dispatcher.Run();
            }
            catch (Exception exception)
            {
                _ready.TrySetException(exception);
            }
        }) { IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public async Task RunAsync(Func<Task> test)
    {
        Dispatcher dispatcher = await _ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await (await dispatcher.InvokeAsync(test)).WaitAsync(TimeSpan.FromSeconds(20));
    }

    public void Dispose()
    {
        if (_ready.Task.IsCompletedSuccessfully)
        {
            _ready.Task.Result.InvokeShutdown();
        }

        _thread.Join(TimeSpan.FromSeconds(5));
    }
}
