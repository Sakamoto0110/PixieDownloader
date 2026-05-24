using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace PixieDownloader.Mvvm;

/// <summary>ICommand síncrono sem parâmetro. Substitui [RelayCommand] em métodos void.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => _execute();
    public void NotifyCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
}

/// <summary>ICommand síncrono com parâmetro tipado (vem do CommandParameter do XAML).</summary>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Func<T?, bool>? _canExecute;

    public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(Cast(parameter)) ?? true;
    public void Execute(object? parameter) => _execute(Cast(parameter));
    public void NotifyCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();

    private static T? Cast(object? p) => p is T t ? t : default;
}

/// <summary>
/// ICommand para métodos async Task. Enquanto a Task roda, CanExecute fica false (sem reentrância),
/// imitando o comportamento padrão do AsyncRelayCommand do CommunityToolkit.
/// Obs.: os métodos async do VM já tratam suas próprias exceções (try/catch internos),
/// então o `async void Execute` é seguro aqui.
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _isExecuting = true;
        CommandManager.InvalidateRequerySuggested();
        try { await _execute(); }
        finally
        {
            _isExecuting = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public void NotifyCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
}
