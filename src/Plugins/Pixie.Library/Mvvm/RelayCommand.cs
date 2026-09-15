using System.Windows.Input;

namespace Pixie.Library.Mvvm;

// The app's RelayCommand lives in the exe, out of a plugin's reach; the Sdk ships only the observable base.
// Same shape as the host's: CanExecuteChanged rides on CommandManager.RequerySuggested.

/// <summary>Synchronous command without a parameter.</summary>
internal sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
}

/// <summary>Synchronous command with a typed parameter (the XAML CommandParameter).</summary>
internal sealed class RelayCommand<T>(Action<T> execute, Func<T, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => parameter is T value && (canExecute?.Invoke(value) ?? true);

    public void Execute(object? parameter)
    {
        if (parameter is T value)
            execute(value);
    }
}
