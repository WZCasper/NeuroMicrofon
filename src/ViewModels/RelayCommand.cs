using System;
using System.Windows.Input;

namespace NeuroMicrophone.ViewModels;

public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    // CommandManager.RequerySuggested переоценивает CanExecute при большинстве
    // UI-событий (клики, смена фокуса и т.п.) — простой и достаточный подход
    // для команд этого приложения, без необходимости вручную дёргать
    // RaiseCanExecuteChanged из ViewModel.
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
