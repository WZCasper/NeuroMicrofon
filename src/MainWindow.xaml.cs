using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using NeuroMicrophone.Models;
using NeuroMicrophone.Services;
using NeuroMicrophone.ViewModels;

namespace NeuroMicrophone;

public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

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
        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Просим Windows нарисовать системную (не WPF) рамку и заголовок окна
        // в тёмном варианте — иначе они остаются светлыми независимо от темы
        // самого приложения, поскольку WPF их не рисует (это зона самой ОС).
        // На версиях Windows, где атрибут не поддерживается, вызов просто
        // ни на что не влияет — это не критично для остальной работы.
        try
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            int useDarkMode = 1;
            DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));
        }
        catch (Exception)
        {
            // Не критично — просто останется светлая рамка на очень старых системах.
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _hotkeyService = new HotkeyService(this, _viewModel.HotkeyModifierFlags, _viewModel.HotkeyVirtualKey);
        _hotkeyService.HotkeyPressed += () => _viewModel.IsMuted = !_viewModel.IsMuted;

        // Если настройки загрузятся асинхронно уже после этого события (или
        // пользователь запишет новую комбинацию через UI) — перерегистрируем хук.
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;

        _trayIconService = new TrayIconService(this);
        _trayIconService.ExitRequested += () =>
        {
            _isExiting = true;
            Close();
        };
    }

    /// <summary>
    /// Активен только пока ViewModel.IsCapturingHotkey == true (после нажатия
    /// кнопки "Записать"). Принимает не более одного модификатора
    /// (Ctrl/Alt/Shift) плюс одну обычную клавишу — то есть сочетание не
    /// длиннее двух клавиш, как и просил пользователь. Клавишу Windows как
    /// модификатор не поддерживаем — такие сочетания зарезервированы ОС.
    /// </summary>
    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_viewModel.IsCapturingHotkey) return;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        bool isModifierKey = key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin
            or Key.System;

        if (isModifierKey)
        {
            e.Handled = true;
            return;
        }

        if (key == Key.Escape)
        {
            _viewModel.CancelHotkeyCapture();
            e.Handled = true;
            return;
        }

        ModifierKeys modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            e.Handled = true;
            return; // Win-сочетания зарезервированы ОС — просим попробовать ещё раз молча.
        }

        int modifierCount = 0;
        if (modifiers.HasFlag(ModifierKeys.Control)) modifierCount++;
        if (modifiers.HasFlag(ModifierKeys.Alt)) modifierCount++;
        if (modifiers.HasFlag(ModifierKeys.Shift)) modifierCount++;

        if (modifierCount > 1)
        {
            // Больше одного модификатора — это уже три клавиши в сочетании.
            // Ничего не применяем, ждём повторного, более короткого нажатия.
            e.Handled = true;
            return;
        }

        uint winModifiers;
        string modifierText;
        if (modifiers.HasFlag(ModifierKeys.Control)) { winModifiers = HotkeyModifiers.Control; modifierText = "Ctrl + "; }
        else if (modifiers.HasFlag(ModifierKeys.Alt)) { winModifiers = HotkeyModifiers.Alt; modifierText = "Alt + "; }
        else if (modifiers.HasFlag(ModifierKeys.Shift)) { winModifiers = HotkeyModifiers.Shift; modifierText = "Shift + "; }
        else { winModifiers = 0; modifierText = ""; }

        uint virtualKey;
        try
        {
            virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        }
        catch (Exception)
        {
            e.Handled = true;
            return;
        }

        string displayText = modifierText + key.ToString().ToUpperInvariant();

        bool registered = _hotkeyService?.ChangeHotkey(winModifiers, virtualKey) ?? false;
        if (registered)
        {
            _viewModel.ApplyCapturedHotkey(winModifiers, virtualKey, displayText);
        }
        else
        {
            // Комбинация уже занята другой программой в системе — восстанавливаем
            // предыдущую рабочую комбинацию и сообщаем пользователю.
            _hotkeyService?.ChangeHotkey(_viewModel.HotkeyModifierFlags, _viewModel.HotkeyVirtualKey);
            _viewModel.ReportHotkeyRegistrationFailed();
        }

        e.Handled = true;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.HotkeyVirtualKey) or nameof(MainViewModel.HotkeyModifierFlags))
        {
            _hotkeyService?.ChangeHotkey(_viewModel.HotkeyModifierFlags, _viewModel.HotkeyVirtualKey);
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
