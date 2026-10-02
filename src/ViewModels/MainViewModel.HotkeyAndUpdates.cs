// MainViewModel: глобальная горячая клавиша заглушки микрофона и проверка
// обновлений приложения. Это два разных, не связанных друг с другом по
// смыслу небольших блока — объединены в один файл, чтобы не плодить файлы
// на 20-30 строк каждый. Вынесено из основного файла по мере его роста.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using NeuroMicrophone.Audio;
using NeuroMicrophone.Driver;
using NeuroMicrophone.Models;
using NeuroMicrophone.Services;

namespace NeuroMicrophone.ViewModels;

public partial class MainViewModel
{
    private readonly UpdateCheckService _updateCheckService = new();

    // --- Горячая клавиша заглушки микрофона: свободная запись, максимум
    //     один модификатор (Ctrl/Alt/Shift) + одна клавиша — то есть не
    //     более двух клавиш в сочетании, как и просил пользователь.
    //     Само нажатие слушает MainWindow (ему нужен доступ к клавиатуре
    //     на уровне окна) и передаёт сюда уже провалидированный результат.

    private uint _hotkeyModifierFlags = HotkeyModifiers.Control;
    public uint HotkeyModifierFlags { get => _hotkeyModifierFlags; private set => SetProperty(ref _hotkeyModifierFlags, value); }

    private uint _hotkeyVirtualKey = 0x4D; // 'M' по умолчанию
    public uint HotkeyVirtualKey { get => _hotkeyVirtualKey; private set => SetProperty(ref _hotkeyVirtualKey, value); }

    private string _hotkeyDisplayText = "Ctrl + M";
    public string HotkeyDisplayText { get => _hotkeyDisplayText; private set => SetProperty(ref _hotkeyDisplayText, value); }

    private bool _isCapturingHotkey;
    public bool IsCapturingHotkey { get => _isCapturingHotkey; set => SetProperty(ref _isCapturingHotkey, value); }

    public ICommand StartHotkeyCaptureCommand { get; }

    /// <summary>Вызывается из MainWindow после того, как WinAPI успешно зарегистрировал новую комбинацию.</summary>
    public void ApplyCapturedHotkey(uint modifiers, uint virtualKey, string displayText)
    {
        HotkeyModifierFlags = modifiers;
        HotkeyVirtualKey = virtualKey;
        HotkeyDisplayText = displayText;
        IsCapturingHotkey = false;
        ScheduleSettingsSave();
    }

    public void CancelHotkeyCapture() => IsCapturingHotkey = false;

    public void ReportHotkeyRegistrationFailed()
    {
        IsCapturingHotkey = false;
        StatusMessage = "Эта комбинация уже занята другой программой. Попробуйте другую.";
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

    // --- Проверка обновлений ---

    private string? _updateAvailableMessage;
    public string? UpdateAvailableMessage { get => _updateAvailableMessage; private set => SetProperty(ref _updateAvailableMessage, value); }

    private string? _updateAvailableUrl;
    public string? UpdateAvailableUrl { get => _updateAvailableUrl; private set => SetProperty(ref _updateAvailableUrl, value); }

    public ICommand OpenUpdateCommand { get; }

    private async Task CheckForUpdatesAsync()
    {
        UpdateCheckResult? result = await _updateCheckService.CheckForUpdateAsync();
        if (result == null) return;

        UpdateAvailableMessage = $"Доступна новая версия {result.NewVersion} — обновите приложение.";
        UpdateAvailableUrl = result.ReleaseUrl;
    }
}
