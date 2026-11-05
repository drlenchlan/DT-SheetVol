using System.Windows.Input;

namespace SheetVol.Core.Presentation;

// ICommand lives in System.ObjectModel (base class library), not in WPF, so Core can
// implement it. Core has no CommandManager: every command raises CanExecuteChanged
// explicitly through RaiseCanExecuteChanged, called by the owning view model.

/// <summary>Synchronous parameterless command.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    /// <summary>Creates the command.</summary>
    /// <param name="execute">Action run by <see cref="Execute"/>.</param>
    /// <param name="canExecute">Guard; always executable when null.</param>
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <inheritdoc/>
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc/>
    public bool CanExecute(object? parameter = null) => _canExecute?.Invoke() ?? true;

    /// <inheritdoc/>
    public void Execute(object? parameter = null)
    {
        if (CanExecute(parameter)) _execute();
    }

    /// <summary>Tells bound controls to re-query <see cref="CanExecute"/>.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Synchronous command with a typed parameter, for example a segment GUID from a chart bar.</summary>
/// <typeparam name="T">Parameter type. A parameter of another type is not executable.</typeparam>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T> _execute;
    private readonly Func<T, bool>? _canExecute;

    /// <summary>Creates the command.</summary>
    /// <param name="execute">Action run by <see cref="Execute"/>.</param>
    /// <param name="canExecute">Guard; always executable for a <typeparamref name="T"/> when null.</param>
    public RelayCommand(Action<T> execute, Func<T, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <inheritdoc/>
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc/>
    public bool CanExecute(object? parameter) => parameter is T t && (_canExecute?.Invoke(t) ?? true);

    /// <inheritdoc/>
    public void Execute(object? parameter)
    {
        if (parameter is T t && CanExecute(t)) _execute(t);
    }

    /// <summary>Tells bound controls to re-query <see cref="CanExecute"/>.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Asynchronous parameterless command. Not re-entrant: it is not executable while a
/// previous execution is running. Tests and code-behind call <see cref="ExecuteAsync"/>;
/// <see cref="Execute"/> exists for binding. The delegate is expected to catch its own
/// exceptions (the view models do, at the command boundary, per HANDOFF.md 6.13).
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _running;

    /// <summary>Creates the command.</summary>
    /// <param name="execute">Asynchronous action.</param>
    /// <param name="canExecute">Guard; always executable when idle and null.</param>
    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <inheritdoc/>
    public event EventHandler? CanExecuteChanged;

    /// <summary>True while an execution is in progress.</summary>
    public bool IsRunning => _running;

    /// <inheritdoc/>
    public bool CanExecute(object? parameter = null) => !_running && (_canExecute?.Invoke() ?? true);

    /// <inheritdoc/>
    public async void Execute(object? parameter = null) => await ExecuteAsync();

    /// <summary>Runs the command when executable and completes when the action completes.</summary>
    public async Task ExecuteAsync()
    {
        if (!CanExecute()) return;
        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute();
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    /// <summary>Tells bound controls to re-query <see cref="CanExecute"/>.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
