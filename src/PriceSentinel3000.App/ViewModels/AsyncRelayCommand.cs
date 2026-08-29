using System.Windows.Input;

namespace PriceSentinel3000.App.ViewModels;

public sealed class AsyncRelayCommand(
    Func<Task> execute,
    Func<bool>? canExecute = null,
    Func<bool>? canExecuteDuringExecution = null) : ICommand
{
    private bool _isExecuting;

    public event EventHandler? CanExecuteChanged;

    public Task? ExecutionTask { get; private set; }

    public bool CanExecute(object? parameter) =>
        (!_isExecuting || (canExecuteDuringExecution?.Invoke() ?? false)) &&
        (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter) => await ExecuteAsync(parameter);

    public Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter))
        {
            return Task.CompletedTask;
        }

        if (_isExecuting)
        {
            return execute();
        }

        _isExecuting = true;
        RaiseCanExecuteChanged();
        ExecutionTask = ExecuteCoreAsync();
        return ExecutionTask;
    }

    private async Task ExecuteCoreAsync()
    {
        try
        {
            await execute();
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
