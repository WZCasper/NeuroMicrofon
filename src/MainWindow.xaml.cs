using System;
using System.ComponentModel;
using System.Windows;
using NeuroMicrophone.Services;
using NeuroMicrophone.ViewModels;

namespace NeuroMicrophone;

public partial class MainWindow : Window
{
    private const uint VirtualKeyM = 0x4D;

    private readonly MainViewModel _viewModel;
    private HotkeyService? _hotkeyService;
    private TrayIconService? _trayIconService;
    private bool _isExiting;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _hotkeyService = new HotkeyService(this, VirtualKeyM);
        _hotkeyService.HotkeyPressed += () => _viewModel.IsMuted = !_viewModel.IsMuted;

        _trayIconService = new TrayIconService(this);
        _trayIconService.ExitRequested += () =>
        {
            _isExiting = true;
            Close();
        };
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            _trayIconService?.ShowInTray();
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isExiting)
        {
            // Закрытие крестиком сворачивает приложение в трей, а не завершает
            // работу — обработка звука должна продолжаться в фоне.
            e.Cancel = true;
            WindowState = WindowState.Minimized;
            return;
        }

        _hotkeyService?.Dispose();
        _trayIconService?.Dispose();

        // Блокирующий вызов здесь безопасен: SettingsService использует
        // ConfigureAwait(false) во всех своих await, поэтому продолжение не
        // пытается вернуться в поток UI, который мы как раз блокируем —
        // взаимной блокировки не возникает. Это гарантирует, что последние
        // изменения настроек (например, только что подвинутый слайдер)
        // действительно попадут на диск перед выходом, а не будут потеряны
        // из-за отложенного (debounced) сохранения.
        _viewModel.FlushSettingsAsync().GetAwaiter().GetResult();
        _viewModel.Dispose();
    }
}
