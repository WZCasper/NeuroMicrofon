using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace NeuroMicrophone;

/// <summary>
/// Точка входа приложения. Отвечает за:
///  - защиту от повторного запуска (два экземпляра не должны одновременно
///    захватывать одно и то же устройство записи через WASAPI);
///  - глобальный перехват необработанных исключений с записью в лог,
///    чтобы приложение не "молча" падало без диагностики.
/// </summary>
public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = "NeuroMicrophone_SingleInstance_Mutex";

    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, name: SingleInstanceMutexName, createdNew: out bool isNewInstance);
        if (!isNewInstance)
        {
            System.Windows.MessageBox.Show(
                "NeuroMicrophone уже запущен. Проверьте значок в области уведомлений.",
                "NeuroMicrophone",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException(e.Exception);

        System.Windows.MessageBox.Show(
            $"Произошла непредвиденная ошибка:\n{e.Exception.Message}\n\nПодробности сохранены в журнал.",
            "NeuroMicrophone — ошибка",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        // Приложение остаётся в живых состоянии: сбой в UI-потоке не должен
        // мгновенно останавливать активный аудиопоток пользователя.
        e.Handled = true;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogException(ex);
        }
    }

    private static void LogException(Exception ex)
    {
        try
        {
            string logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NeuroMicrophone",
                "logs");

            Directory.CreateDirectory(logDirectory);

            string logPath = Path.Combine(logDirectory, $"error_{DateTime.Now:yyyyMMdd_HHmmss_fff}.log");
            File.WriteAllText(logPath, ex.ToString());
        }
        catch
        {
            // Сбой логирования не должен приводить к повторному падению приложения.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Мьютекс мог быть уже освобождён или не принадлежать текущему потоку — это не критично при выходе.
        }
        finally
        {
            _singleInstanceMutex?.Dispose();
        }

        base.OnExit(e);
    }
}
