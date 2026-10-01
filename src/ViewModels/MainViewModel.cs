// Ядро MainViewModel: жизненный цикл движка обработки звука (запуск/
// перезапуск/остановка), выбор устройств ввода-вывода-прослушивания, живые
// метры уровня сигнала, конструктор (создаёт все команды и сервисы) и
// Dispose. Калибровка, DSP-параметры, настройки, драйвер, горячая клавиша и
// проверка обновлений вынесены в отдельные partial-файлы MainViewModel.*.cs —
// см. их для соответствующей функциональности.

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

public sealed partial class MainViewModel : ViewModelBase, IDisposable
{
    private const string DriverInfFileName = "NeuroMicCable.inf";

    private readonly AudioEngine _engine = new();
    private readonly DriverInstaller _driverInstaller = new();

    private readonly UpdateCheckService _updateCheckService = new();
    private readonly DispatcherTimer _meterTimer;
    private readonly DispatcherTimer _saveDebounceTimer;

    private string? _publishedDriverInfName;

    public ObservableCollection<AudioDeviceInfo> InputDevices { get; } = new();
    public ObservableCollection<AudioDeviceInfo> OutputDevices { get; } = new();

    private AudioDeviceInfo? _selectedInputDevice;
    public AudioDeviceInfo? SelectedInputDevice
    {
        get => _selectedInputDevice;
        set
        {
            if (SetProperty(ref _selectedInputDevice, value))
            {
                RestartEngine();
                ScheduleSettingsSave();
            }
        }
    }

    private AudioDeviceInfo? _selectedOutputDevice;
    public AudioDeviceInfo? SelectedOutputDevice
    {
        get => _selectedOutputDevice;
        set
        {
            if (SetProperty(ref _selectedOutputDevice, value))
            {
                RestartEngine();
                ScheduleSettingsSave();
            }
        }
    }

    private AudioDeviceInfo? _selectedMonitorDevice;
    public AudioDeviceInfo? SelectedMonitorDevice
    {
        get => _selectedMonitorDevice;
        set
        {
            if (!SetProperty(ref _selectedMonitorDevice, value)) return;
            ScheduleSettingsSave();

            if (IsMonitoring && value != null)
            {
                try
                {
                    _engine.StartMonitoring(value.Id);
                }
                catch (Exception ex)
                {
                    StatusMessage = $"Не удалось переключить устройство прослушивания: {ex.Message}";
                }
            }
        }
    }

    private double _rmsLevel = LevelMeter.MinDb;
    public double RmsLevel { get => _rmsLevel; private set => SetProperty(ref _rmsLevel, value); }

    private double _peakLevel = LevelMeter.MinDb;
    public double PeakLevel { get => _peakLevel; private set => SetProperty(ref _peakLevel, value); }

    private double _inputLevel = LevelMeter.MinDb;
    /// <summary>RMS-уровень "сырого" сигнала до всей DSP-цепочки (для индикатора входа).</summary>
    public double InputLevel { get => _inputLevel; private set => SetProperty(ref _inputLevel, value); }

    private double _compressorGainReductionDb;
    public double CompressorGainReductionDb { get => _compressorGainReductionDb; private set => SetProperty(ref _compressorGainReductionDb, value); }

    private double _limiterGainReductionDb;
    public double LimiterGainReductionDb { get => _limiterGainReductionDb; private set => SetProperty(ref _limiterGainReductionDb, value); }

    private string? _statusMessage;
    public string? StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    private bool _isDriverInstalled;
    public bool IsDriverInstalled { get => _isDriverInstalled; private set => SetProperty(ref _isDriverInstalled, value); }

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
    public ICommand OpenVbCableLinkCommand { get; }

    public ICommand InstallDriverCommand { get; }
    public ICommand UninstallDriverCommand { get; }

    public MainViewModel()
    {
        AutoTuneCommand = new RelayCommand(async () => await RunCalibrationAsync(), () => !IsCalibrating && _engine.IsRunning);
        ToggleMuteCommand = new RelayCommand(() => IsMuted = !IsMuted);
        InstallDriverCommand = new RelayCommand(async () => await InstallDriverAsync(), () => !IsDriverInstalled);
        UninstallDriverCommand = new RelayCommand(async () => await UninstallDriverAsync(), () => IsDriverInstalled);
        SelectPresetCommand = new RelayCommand<DspPreset>(preset =>
        {
            if (preset != null) SelectedPreset = preset;
        });
        StartHotkeyCaptureCommand = new RelayCommand(() => IsCapturingHotkey = true);
        RecallCalibrationCommand = new RelayCommand(RecallCalibration, () => HasCalibrationResult);
        OpenUpdateCommand = new RelayCommand(() =>
        {
            if (string.IsNullOrEmpty(UpdateAvailableUrl)) return;
            Process.Start(new ProcessStartInfo(UpdateAvailableUrl) { UseShellExecute = true });
        });
        OpenVbCableLinkCommand = new RelayCommand(() =>
            Process.Start(new ProcessStartInfo("https://vb-audio.com/Cable/") { UseShellExecute = true }));

        _engine.ErrorOccurred += (_, message) => StatusMessage = message;

        // ВАЖНО: таймер debounce-сохранения должен существовать ДО того, как
        // ниже будут выставлены SelectedInputDevice/SelectedOutputDevice/
        // SelectedMonitorDevice — их сеттеры вызывают ScheduleSettingsSave(),
        // который обращается к _saveDebounceTimer. Обратный порядок приводит
        // к NullReferenceException прямо при создании ViewModel (то есть к
        // падению приложения на самом старте).
        _saveDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveDebounceTimer.Tick += async (_, _) =>
        {
            _saveDebounceTimer.Stop();
            await SaveSettingsAsync();
        };

        RefreshDeviceLists();
        IsDriverInstalled = _driverInstaller.IsDriverInstalled();
        IsAutostartEnabled = AutostartService.IsEnabled();

        SelectedInputDevice = InputDevices.FirstOrDefault();
        // ВАЖНО: если виртуальный кабель не найден, НЕ выбираем случайное
        // реальное устройство (колонки/наушники) — иначе движок начнёт
        // отправлять туда обработанный сигнал микрофона, и пользователь
        // будет слышать сам себя (это и была причина жалобы "слышу себя
        // постоянно"). Лучше оставить вывод пустым и явно попросить выбрать
        // устройство или установить виртуальный кабель.
        SelectedOutputDevice =
            OutputDevices.FirstOrDefault(d => d.Name.Contains(DriverInstaller.VirtualDeviceName, StringComparison.OrdinalIgnoreCase));
        SelectedMonitorDevice = null;

        _meterTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33), // ~30 кадров/с — достаточно для плавных индикаторов
        };
        _meterTimer.Tick += (_, _) =>
        {
            RmsLevel = _engine.ProcessedRmsDb;
            PeakLevel = _engine.ProcessedPeakDb;
            InputLevel = _engine.RawRmsDb;
            CompressorGainReductionDb = _engine.Pipeline?.Comp.CurrentGainReductionDb ?? 0.0;
            LimiterGainReductionDb = _engine.Pipeline?.Lim.CurrentGainReductionDb ?? 0.0;
        };
        _meterTimer.Start();

        _ = LoadSettingsAndApplyAsync();
        _ = CheckForUpdatesAsync();
    }

    private void RefreshDeviceLists()
    {
        InputDevices.Clear();
        foreach (var device in AudioEngine.GetInputDevices())
        {
            InputDevices.Add(device);
        }

        OutputDevices.Clear();
        foreach (var device in AudioEngine.GetOutputDevices())
        {
            OutputDevices.Add(device);
        }
    }

    private void RestartEngine()
    {
        if (SelectedInputDevice == null || SelectedOutputDevice == null) return;

        try
        {
            _engine.Start(SelectedInputDevice.Id, SelectedOutputDevice.Id);
            ApplyCurrentDspSettingsToPipeline();
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Не удалось запустить обработку звука: {ex.Message}";
        }
    }

    /// <summary>
    /// Каждый перезапуск движка создаёт новый DspPipeline со значениями по
    /// умолчанию — эта функция переносит на него текущие (сохранённые/
    /// откалиброванные/выбранные из пресета) параметры ViewModel.
    /// </summary>
    private void ApplyCurrentDspSettingsToPipeline()
    {
        DspPipeline? dsp = _engine.Pipeline;
        if (dsp == null) return;

        dsp.Gate.ThresholdDb = GateThresholdDb;
        dsp.Denoiser.WetMix = DenoiserWetMix;
        dsp.Agc.TargetLevelDb = AgcTargetLevelDb;
        dsp.Comp.ThresholdDb = CompressorThresholdDb;
        dsp.Comp.Ratio = CompressorRatio;
        dsp.HighPass.CutoffHz = HighPassCutoffHz;

        dsp.Denoiser.Enabled = DenoiserEnabled;
        dsp.Gate.Enabled = GateEnabled;
        dsp.Agc.Enabled = AgcEnabled;
        dsp.Comp.Enabled = CompEnabled;
    }

    private async Task CheckForUpdatesAsync()
    {
        UpdateCheckResult? result = await _updateCheckService.CheckForUpdateAsync();
        if (result == null) return;

        UpdateAvailableMessage = $"Доступна новая версия {result.NewVersion} — обновите приложение.";
        UpdateAvailableUrl = result.ReleaseUrl;
    }

    private async Task InstallDriverAsync()
    {
        string infPath = Path.Combine(AppContext.BaseDirectory, "Driver", "Package", DriverInfFileName);
        DriverInstallResult result = await _driverInstaller.InstallDriverAsync(infPath);

        if (result.IsSuccess)
        {
            _publishedDriverInfName = result.PublishedInfName;
            IsDriverInstalled = _driverInstaller.IsDriverInstalled();
            StatusMessage = null;
            ScheduleSettingsSave();
        }
        else
        {
            StatusMessage = result.Message;
        }
    }

    private async Task UninstallDriverAsync()
    {
        DriverInstallResult result = await _driverInstaller.UninstallDriverAsync(_publishedDriverInfName);

        if (result.IsSuccess)
        {
            _publishedDriverInfName = null;
            IsDriverInstalled = _driverInstaller.IsDriverInstalled();
            ScheduleSettingsSave();
        }
        else
        {
            StatusMessage = result.Message;
        }
    }

    public void Dispose()
    {
        _meterTimer.Stop();
        _saveDebounceTimer.Stop();
        _calibrationCts?.Cancel();
        _calibrationCts?.Dispose();
        _engine.Dispose();
    }
}
