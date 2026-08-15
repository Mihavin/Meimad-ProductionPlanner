using System.Windows.Input;

namespace Meimad.Planner.Client.Windows.Presentation;

public sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> execute;
    private readonly Func<bool> canExecute;
    private bool isExecuting;

    internal AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        this.execute = execute;
        this.canExecute = canExecute ?? (() => true);
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !isExecuting && canExecute();

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        isExecuting = true;
        RaiseCanExecuteChanged();
        try
        {
            await execute();
        }
        finally
        {
            isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    internal void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class AsyncCommand<T> : ICommand
    where T : class
{
    private readonly Func<T?, Task> execute;
    private readonly Func<T?, bool> canExecute;
    private bool isExecuting;

    internal AsyncCommand(Func<T?, Task> execute, Func<T?, bool>? canExecute = null)
    {
        this.execute = execute;
        this.canExecute = canExecute ?? (_ => true);
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !isExecuting && canExecute(parameter as T);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        isExecuting = true;
        RaiseCanExecuteChanged();
        try
        {
            await execute(parameter as T);
        }
        finally
        {
            isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    internal void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
