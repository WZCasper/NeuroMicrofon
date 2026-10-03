// MainViewModel: сохранение и загрузка пользовательских настроек на диск
// (устройства, DSP-параметры, калибровка, горячая клавиша, автозапуск) —
// вынесено из основного файла по мере его роста.

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
    private readonly SettingsService _settingsService = new();

    private bool _isLoadingSettings;

    private bool _isAutostartEnabled;
    public bool IsAutostartEnabled
    {
        get => _isAutostartEnabled;
        set
        {
            if (!SetProperty(ref _isAutostartEnabled, value)) return;
            if (_isLoadingSettings) return;

            string exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "NeuroMicrophone.exe");
            AutostartService.SetEnabled(value, exePath);
            ScheduleSettingsSave();
        }
    }

    private async Task LoadSettingsAndApplyAsync()
    {
        AppSettings? settings = await _settingsService.LoadAsync();
        if (settings == null) return;

        _isLoadingSettings = true;
        try
        {
            AudioDeviceInfo? savedInput = InputDevices.FirstOrDefault(d => d.Id == settings.InputDeviceId);
            if (savedInput != null) SelectedInputDevice = savedInput;

            AudioDeviceInfo? savedOutput = OutputDevices.FirstOrDefault(d => d.Id == settings.OutputDeviceId);
            if (savedOutput != null) SelectedOutputDevice = savedOutput;

            AudioDeviceInfo? savedMonitor = OutputDevices.FirstOrDefault(d => d.Id == settings.MonitorDeviceId);
            if (savedMonitor != null) SelectedMonitorDevice = savedMonitor;

            if (settings.HasDspSettings)
            {
                Dsp.GateThresholdDb = settings.GateThresholdDb;
                Dsp.DenoiserWetMix = settings.DenoiserWetMix;
                Dsp.AgcTargetLevelDb = settings.AgcTargetLevelDb;
                Dsp.CompressorThresholdDb = settings.CompressorThresholdDb;
                Dsp.CompressorRatio = settings.CompressorRatio;
                Dsp.HighPassCutoffHz = settings.HighPassCutoffHz;
            }

            if (settings.HasCalibrationResult)
            {
                _calibratedGateThresholdDb = settings.CalibratedGateThresholdDb;
                _calibratedWetMix = settings.CalibratedWetMix;
                _calibratedCompThresholdDb = settings.CalibratedCompThresholdDb;
                _calibratedCompRatio = settings.CalibratedCompRatio;
                HasCalibrationResult = true;
            }

            if (settings.HotkeyModifiers != 0 && settings.HotkeyVirtualKey != 0)
            {
                HotkeyAndUpdates.LoadHotkey(settings.HotkeyModifiers, settings.HotkeyVirtualKey);
            }

            Driver.LoadPublishedDriverInfName(settings.PublishedDriverInfName);
            IsAutostartEnabled = settings.LaunchOnStartup;
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    private void ScheduleSettingsSave()
    {
        if (_isLoadingSettings) return;
        _saveDebounceTimer.Stop();
        _saveDebounceTimer.Start();
    }

    private async Task SaveSettingsAsync()
    {
        var settings = new AppSettings
        {
            InputDeviceId = SelectedInputDevice?.Id,
            OutputDeviceId = SelectedOutputDevice?.Id,
            MonitorDeviceId = SelectedMonitorDevice?.Id,
            GateThresholdDb = Dsp.GateThresholdDb,
            DenoiserWetMix = Dsp.DenoiserWetMix,
            AgcTargetLevelDb = Dsp.AgcTargetLevelDb,
            CompressorThresholdDb = Dsp.CompressorThresholdDb,
            CompressorRatio = Dsp.CompressorRatio,
            HighPassCutoffHz = Dsp.HighPassCutoffHz,
            HotkeyModifiers = HotkeyAndUpdates.HotkeyModifierFlags,
            HotkeyVirtualKey = HotkeyAndUpdates.HotkeyVirtualKey,
            LaunchOnStartup = IsAutostartEnabled,
            PublishedDriverInfName = Driver.PublishedDriverInfName,
            HasDspSettings = true,
            HasCalibrationResult = HasCalibrationResult,
            CalibratedGateThresholdDb = _calibratedGateThresholdDb,
            CalibratedWetMix = _calibratedWetMix,
            CalibratedCompThresholdDb = _calibratedCompThresholdDb,
            CalibratedCompRatio = _calibratedCompRatio,
        };

        // ConfigureAwait(false) обязателен здесь: MainWindow_Closing вызывает
        // FlushSettingsAsync().GetAwaiter().GetResult() синхронно, блокируя
        // поток UI. Без ConfigureAwait(false) продолжение после await
        // попыталось бы вернуться в тот же (заблокированный) поток UI через
        // SynchronizationContext — это классический ASP.NET/WPF deadlock.
        // Гарантия должна выполняться на каждом шаге всей цепочки await,
        // а не только "по факту" в её нынешней реализации — поэтому
        // ConfigureAwait(false) стоит и здесь, и в FlushSettingsAsync ниже.
        await _settingsService.SaveAsync(settings).ConfigureAwait(false);
    }

    /// <summary>Принудительно сбрасывает отложенное сохранение — вызывается при закрытии приложения.</summary>
    public async Task FlushSettingsAsync()
    {
        _saveDebounceTimer.Stop();
        await SaveSettingsAsync().ConfigureAwait(false);
    }
}
