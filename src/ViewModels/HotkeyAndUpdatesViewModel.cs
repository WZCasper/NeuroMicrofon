// Этап B разбиения MainViewModel: настоящий дочерний ViewModel для горячей
// клавиши заглушки микрофона и проверки обновлений приложения. Как и на
// Этапе A, это два разных, не связанных друг с другом по смыслу небольших
// блока — объединены в один класс, чтобы не плодить классы на 20-30 строк
// каждый.
//
// ВАЖНО для MainWindow: само нажатие клавиш слушает окно (ему нужен доступ
// к клавиатуре на уровне окна), поэтому код-behind обращается к свойствам и
// методам этого класса напрямую через MainViewModel.HotkeyAndUpdates, а на
// изменение HotkeyModifierFlags/HotkeyVirtualKey подписывается через
// HotkeyAndUpdates.PropertyChanged — не через PropertyChanged самого
// MainViewModel, поскольку теперь это разные объекты, каждый со своим
// событием.

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Input;
using NeuroMicrophone.Driver;
using NeuroMicrophone.Models;
using NeuroMicrophone.Services;

namespace NeuroMicrophone.ViewModels;

public sealed class HotkeyAndUpdatesViewModel : ViewModelBase
{
    private readonly UpdateCheckService _updateCheckService = new();
    private readonly Action<string?> _setStatusMessage;
    private readonly Action _scheduleSettingsSave;

    // --- Горячая клавиша заглушки микрофона: свободная запись, максимум
    //     один модификатор (Ctrl/Alt/Shift) + одна клавиша — то есть не
    //     более двух клавиш в сочетании, как и просил пользователь. ---

    private uint _hotkeyModifierFlags = HotkeyModifiers.Control;
    public uint HotkeyModifierFlags { get => _hotkeyModifierFlags; private set => SetProperty(ref _hotkeyModifierFlags, value); }

    private uint _hotkeyVirtualKey = 0x4D; // 'M' по умолчанию
    public uint HotkeyVirtualKey { get => _hotkeyVirtualKey; private set => SetProperty(ref _hotkeyVirtualKey, value); }

    private string _hotkeyDisplayText = "Ctrl + M";
    public string HotkeyDisplayText { get => _hotkeyDisplayText; private set => SetProperty(ref _hotkeyDisplayText, value); }

    private bool _isCapturingHotkey;
    public bool IsCapturingHotkey { get => _isCapturingHotkey; set => SetProperty(ref _isCapturingHotkey, value); }

    public ICommand StartHotkeyCaptureCommand { get; }

    // --- Проверка обновлений ---

    private string? _updateAvailableMessage;
    public string? UpdateAvailableMessage { get => _updateAvailableMessage; private set => SetProperty(ref _updateAvailableMessage, value); }

    private string? _updateAvailableUrl;
    public string? UpdateAvailableUrl { get => _updateAvailableUrl; private set => SetProperty(ref _updateAvailableUrl, value); }

    public ICommand OpenUpdateCommand { get; }

    public HotkeyAndUpdatesViewModel(Action<string?> setStatusMessage, Action scheduleSettingsSave)
    {
        _setStatusMessage = setStatusMessage;
        _scheduleSettingsSave = scheduleSettingsSave;

        StartHotkeyCaptureCommand = new RelayCommand(() => IsCapturingHotkey = true);
        OpenUpdateCommand = new RelayCommand(() =>
        {
            if (string.IsNullOrEmpty(UpdateAvailableUrl)) return;
            Process.Start(new ProcessStartInfo(UpdateAvailableUrl) { UseShellExecute = true });
        });

        _ = CheckForUpdatesAsync();
    }

    /// <summary>Вызывается из MainWindow после того, как WinAPI успешно зарегистрировал новую комбинацию.</summary>
    public void ApplyCapturedHotkey(uint modifiers, uint virtualKey, string displayText)
    {
        HotkeyModifierFlags = modifiers;
        HotkeyVirtualKey = virtualKey;
        HotkeyDisplayText = displayText;
        IsCapturingHotkey = false;
        _scheduleSettingsSave();
    }

    public void CancelHotkeyCapture() => IsCapturingHotkey = false;

    public void ReportHotkeyRegistrationFailed()
    {
        IsCapturingHotkey = false;
        _setStatusMessage("Эта комбинация уже занята другой программой. Попробуйте другую.");
    }

    /// <summary>
    /// Вызывается из MainViewModel.LoadSettingsAndApplyAsync при загрузке
    /// ранее сохранённой комбинации — на момент создания этого класса
    /// настройки ещё не загружены (загрузка асинхронная и идёт уже после
    /// конструктора), поэтому значение приходит отдельным вызовом.
    /// </summary>
    public void LoadHotkey(uint modifiers, uint virtualKey)
    {
        HotkeyModifierFlags = modifiers;
        HotkeyVirtualKey = virtualKey;
        HotkeyDisplayText = BuildHotkeyDisplayText(modifiers, virtualKey);
    }

    private static string BuildHotkeyDisplayText(uint modifiers, uint virtualKey)
    {
        string modifierText = modifiers switch
        {
            HotkeyModifiers.Control => "Ctrl + ",
            HotkeyModifiers.Alt => "Alt + ",
            HotkeyModifiers.Shift => "Shift + ",
            _ => "",
        };

        try
        {
            Key key = KeyInterop.KeyFromVirtualKey((int)virtualKey);
            return modifierText + key.ToString().ToUpperInvariant();
        }
        catch (Exception)
        {
            return modifierText + "?";
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        UpdateCheckResult? result = await _updateCheckService.CheckForUpdateAsync();
        if (result == null) return;

        UpdateAvailableMessage = $"Доступна новая версия {result.NewVersion} — обновите приложение.";
        UpdateAvailableUrl = result.ReleaseUrl;
    }
}
