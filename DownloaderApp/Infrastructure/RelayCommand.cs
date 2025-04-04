namespace DownloaderApp.Infrastructure;

using System;
using System.Windows.Input;

// Простая реализация ICommand (замените на реализацию из MVVM фреймворка)
public class RelayCommand : ICommand
{
    private readonly Action<object> _execute; // Изменено на Action<object>
    private readonly Predicate<object> _canExecute; // Изменено на Predicate<object>

    public event EventHandler CanExecuteChanged
    {
        add { CommandManager.RequerySuggested += value; }
        remove { CommandManager.RequerySuggested -= value; }
    }

    public RelayCommand(Action<object> execute, Predicate<object> canExecute = null) // Принимаем Action<object>
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    // Перегрузка для Action без параметра
    public RelayCommand(Action execute, Func<bool> canExecute = null) 
        : this(_ => execute(), canExecute == null ? (Predicate<object>)null : _ => canExecute())
    {
    }

    public bool CanExecute(object parameter) => _canExecute == null || _canExecute(parameter);

    public void Execute(object parameter) => _execute(parameter);

    public void RaiseCanExecuteChanged()
    {
        // В WPF используем CommandManager для уведомления UI об изменении состояния команды
        CommandManager.InvalidateRequerySuggested();
    }
} 