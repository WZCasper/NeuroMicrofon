using System;
using System.ComponentModel;
using System.Windows;
using NeuroMicrophone.Services;
using NeuroMicrophone.ViewModels;

namespace NeuroMicrophone;

public partial class MainWindow : Window
{
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
        _hotkeyService = new HotkeyService(this, _viewModel.SelectedHotkey.Modifiers, _viewModel.SelectedHotkey.VirtualKey);
        _hotkeyService.HotkeyPressed += () => _viewModel.IsMuted = !_viewModel.IsMuted;

        // Если пользователь выберет другую комбинацию в настройках (в том числе
        // сразу после загрузки сохранённых настроек, которая идёт асинхронно и
        // может завершиться уже после этого события) — перерегистрируем хук.
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;

        _trayIconService = new TrayIconService(this);
        _trayIconService.ExitRequested += () =>
        {
            _isExiting = true;
            Close();
        };
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedHotkey))
        {
            _hotkeyService?.ChangeHotkey(_viewModel.SelectedHotkey.Modifiers, _viewModel.SelectedHotkey.VirtualKey);
        }
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

        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
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
