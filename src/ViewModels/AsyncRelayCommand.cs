using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace NeuroMicrophone.ViewModels;

/// <summary>
/// RelayCommand для асинхронных обработчиков. В отличие от оборачивания
/// async-лямбды в обычный RelayCommand (Action), эта команда сама не даёт
/// запустить себя повторно, пока предыдущий запуск ещё не завершился —
/// CanExecute возвращает false, пока IsExecuting истинно. Без этой защиты
/// быстрый двойной клик (например, по кнопке "Установить драйвер") мог
/// запустить вторую операцию параллельно с первой — второй процесс
/// pnputil.exe и второй запрос прав администратора, пока первый ещё
/// выполняется.
///
/// Execute() объявлен как async void — это единственное место, где это
/// оправдано: сам интерфейс ICommand требует void, а не Task, другого
/// способа подписаться на асинхронный обработчик у ICommand нет. Это тот
/// же приём, которым пользуются готовые реализации AsyncCommand в
/// известных MVVM-библиотеках. Необработанное исключение из обработчика
/// никуда не теряется: если onError не задан, оно всплывает через
/// SynchronizationContext потока UI и доходит до уже существующего
/// Application.DispatcherUnhandledException (см. App.xaml.cs) — то есть
/// ведёт себя точно так же, как и раньше с обычным RelayCommand(async () =>
/// ...). Если onError задан, он получает исключение вместо этого — так
/// вызывающий код может показать его, например, через StatusMessage.
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _executeAsync;
    private readonly Func<bool>? _canExecute;
    private readonly Action<Exception>? _onError;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<Task> executeAsync, Func<bool>? canExecute = null, Action<Exception>? onError = null)
    {
        _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        _canExecute = canExecute;
        _onError = onError;
    }

    public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;

        _isExecuting = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await _executeAsync();
        }
        catch (Exception ex) when (_onError != null)
        {
            _onError!(ex);
        }
        finally
        {
            _isExecuting = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    // CommandManager.RequerySuggested переоценивает CanExecute при большинстве
    // UI-событий — тот же подход, что и в обычном RelayCommand. Явный вызов
    // InvalidateRequerySuggested() выше, сразу при изменении _isExecuting, не
    // полагается на то, что подходящее UI-событие случится само по себе
    // между началом и концом операции (так уже сделано в ObsViewModel).
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
