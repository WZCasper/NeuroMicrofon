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

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private const string DriverInfFileName = "NeuroMicCable.inf";

    private readonly AudioEngine _engine = new();
    private readonly DriverInstaller _driverInstaller = new();
    private readonly SettingsService _settingsService = new();
    private readonly UpdateCheckService _updateCheckService = new();
    private readonly DispatcherTimer _meterTimer;
    private readonly DispatcherTimer _saveDebounceTimer;
    private CancellationTokenSource? _calibrationCts;
    private string? _publishedDriverInfName;
    private bool _isLoadingSettings;

    public ObservableCollection<AudioDeviceInfo> InputDevices { get; } = new();
    public ObservableCollection<AudioDeviceInfo> OutputDevices { get; } = new();

    public IReadOnlyList<DspPreset> Presets => DspPreset.BuiltIn;

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

    // --- Включение/отключение отдельных модулей DSP-цепочки (визуальная "цепочка обработки") ---

    private bool _denoiserEnabled = true;
    public bool DenoiserEnabled
    {
        get => _denoiserEnabled;
        set
        {
            if (!SetProperty(ref _denoiserEnabled, value)) return;
            if (_engine.Pipeline != null) _engine.Pipeline.Denoiser.Enabled = value;
        }
    }

    private bool _gateEnabled = true;
    public bool GateEnabled
    {
        get => _gateEnabled;
        set
        {
            if (!SetProperty(ref _gateEnabled, value)) return;
            if (_engine.Pipeline != null) _engine.Pipeline.Gate.Enabled = value;
        }
    }

    private bool _agcEnabled = true;
    public bool AgcEnabled
    {
        get => _agcEnabled;
        set
        {
            if (!SetProperty(ref _agcEnabled, value)) return;
            if (_engine.Pipeline != null) _engine.Pipeline.Agc.Enabled = value;
        }
    }

    private bool _compEnabled = true;
    public bool CompEnabled
    {
        get => _compEnabled;
        set
        {
            if (!SetProperty(ref _compEnabled, value)) return;
            if (_engine.Pipeline != null) _engine.Pipeline.Comp.Enabled = value;
        }
    }

    private bool _isMuted;
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (SetProperty(ref _isMuted, value)) _engine.IsMuted = value;
        }
    }

    private bool _isMonitoring;
    public bool IsMonitoring
    {
        get => _isMonitoring;
        set
        {
            if (!SetProperty(ref _isMonitoring, value)) return;

            if (value)
            {
                if (SelectedMonitorDevice == null)
                {
                    StatusMessage = "Выберите устройство для прослушивания.";
                    _isMonitoring = false;
                    OnPropertyChanged(nameof(IsMonitoring));
                    return;
                }

                try
                {
                    _engine.StartMonitoring(SelectedMonitorDevice.Id);
                }
                catch (Exception ex)
                {
                    StatusMessage = $"Не удалось включить прослушивание: {ex.Message}";
                    _isMonitoring = false;
                    OnPropertyChanged(nameof(IsMonitoring));
                }
            }
            else
            {
                _engine.StopMonitoring();
            }
        }
    }

    private bool _isCalibrating;
    public bool IsCalibrating { get => _isCalibrating; private set => SetProperty(ref _isCalibrating, value); }

    private double _calibrationProgress;
    public double CalibrationProgress { get => _calibrationProgress; private set => SetProperty(ref _calibrationProgress, value); }

    private string _calibrationInstruction = "Нажмите «Автонастройка», чтобы откалибровать микрофон.";
    public string CalibrationInstruction { get => _calibrationInstruction; private set => SetProperty(ref _calibrationInstruction, value); }

    private string? _statusMessage;
    public string? StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    private bool _isDriverInstalled;
    public bool IsDriverInstalled { get => _isDriverInstalled; private set => SetProperty(ref _isDriverInstalled, value); }

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

    // --- Расширенные настройки DSP: прокси-свойства поверх текущего DspPipeline ---

    // true только на время программного применения пресета/калибровки —
    // отличает эти изменения от ручного перетаскивания ползунка пользователем,
    // чтобы правильно показывать ActivePresetLabel ("Пользовательские
    // настройки" появляется, только если ползунок подвинул сам пользователь).
    private bool _isApplyingPresetOrCalibration;

    private string _activePresetLabel = "Пользовательские настройки";
    public string ActivePresetLabel { get => _activePresetLabel; private set => SetProperty(ref _activePresetLabel, value); }

    private bool _hasCalibrationResult;
    public bool HasCalibrationResult { get => _hasCalibrationResult; private set => SetProperty(ref _hasCalibrationResult, value); }

    private float _calibratedGateThresholdDb;
    private float _calibratedWetMix;
    private float _calibratedCompThresholdDb;
    private float _calibratedCompRatio;

    public ICommand RecallCalibrationCommand { get; }

    private DspPreset? _selectedPreset;
    public DspPreset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!SetProperty(ref _selectedPreset, value) || value == null) return;

            _isApplyingPresetOrCalibration = true;
            try
            {
                GateThresholdDb = value.GateThresholdDb;
                DenoiserWetMix = value.DenoiserWetMix;
                AgcTargetLevelDb = value.AgcTargetLevelDb;
                CompressorThresholdDb = value.CompressorThresholdDb;
                CompressorRatio = value.CompressorRatio;
            }
            finally
            {
                _isApplyingPresetOrCalibration = false;
            }

            ActivePresetLabel = value.Name;
        }
    }

    private void RecallCalibration()
    {
        if (!HasCalibrationResult) return;

        // Сбрасываем визуальный выбор карточки пресета — активна "своя" калибровка, а не один из фиксированных пресетов.
        _selectedPreset = null;
        OnPropertyChanged(nameof(SelectedPreset));

        _isApplyingPresetOrCalibration = true;
        try
        {
            GateThresholdDb = _calibratedGateThresholdDb;
            DenoiserWetMix = _calibratedWetMix;
            AgcTargetLevelDb = -18f;
            CompressorThresholdDb = _calibratedCompThresholdDb;
            CompressorRatio = _calibratedCompRatio;
        }
        finally
        {
            _isApplyingPresetOrCalibration = false;
        }

        ActivePresetLabel = "Автонастройка (моя калибровка)";
    }

    private float _gateThresholdDb = -50f;
    public float GateThresholdDb
    {
        get => _gateThresholdDb;
        set
        {
            if (!SetProperty(ref _gateThresholdDb, value)) return;
            if (_engine.Pipeline != null) _engine.Pipeline.Gate.ThresholdDb = value;
            MarkCustomizedIfUserEdited();
            ScheduleSettingsSave();
        }
    }

    private float _denoiserWetMix = 1f;
    public float DenoiserWetMix
    {
        get => _denoiserWetMix;
        set
        {
            if (!SetProperty(ref _denoiserWetMix, value)) return;
            if (_engine.Pipeline != null) _engine.Pipeline.Denoiser.WetMix = value;
            MarkCustomizedIfUserEdited();
            ScheduleSettingsSave();
        }
    }

    private float _agcTargetLevelDb = -18f;
    public float AgcTargetLevelDb
    {
        get => _agcTargetLevelDb;
        set
        {
            if (!SetProperty(ref _agcTargetLevelDb, value)) return;
            if (_engine.Pipeline != null) _engine.Pipeline.Agc.TargetLevelDb = value;
            MarkCustomizedIfUserEdited();
            ScheduleSettingsSave();
        }
    }

    private float _compressorThresholdDb = -12f;
    public float CompressorThresholdDb
    {
        get => _compressorThresholdDb;
        set
        {
            if (!SetProperty(ref _compressorThresholdDb, value)) return;
            if (_engine.Pipeline != null) _engine.Pipeline.Comp.ThresholdDb = value;
            MarkCustomizedIfUserEdited();
            ScheduleSettingsSave();
        }
    }

    private float _compressorRatio = 3f;
    public float CompressorRatio
    {
        get => _compressorRatio;
        set
        {
            if (!SetProperty(ref _compressorRatio, value)) return;
            if (_engine.Pipeline != null) _engine.Pipeline.Comp.Ratio = value;
            MarkCustomizedIfUserEdited();
            ScheduleSettingsSave();
        }
    }

    private float _highPassCutoffHz = 90f;
    public float HighPassCutoffHz
    {
        get => _highPassCutoffHz;
        set
        {
            if (!SetProperty(ref _highPassCutoffHz, value)) return;
            if (_engine.Pipeline != null) _engine.Pipeline.HighPass.CutoffHz = value;
            MarkCustomizedIfUserEdited();
            ScheduleSettingsSave();
        }
    }

    /// <summary>
    /// Помечает текущий набор настроек как "пользовательский" — но только
    /// если изменение действительно пришло от пользователя (перетаскивание
    /// ползунка), а не от применения пресета/калибровки/загрузки настроек.
    /// </summary>
    private void MarkCustomizedIfUserEdited()
    {
        if (_isApplyingPresetOrCalibration || _isLoadingSettings) return;
        ActivePresetLabel = "Пользовательские настройки";
    }

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

    public ICommand AutoTuneCommand { get; }
    public ICommand ToggleMuteCommand { get; }
    public ICommand InstallDriverCommand { get; }
    public ICommand UninstallDriverCommand { get; }
    public ICommand SelectPresetCommand { get; }

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
                GateThresholdDb = settings.GateThresholdDb;
                DenoiserWetMix = settings.DenoiserWetMix;
                AgcTargetLevelDb = settings.AgcTargetLevelDb;
                CompressorThresholdDb = settings.CompressorThresholdDb;
                CompressorRatio = settings.CompressorRatio;
                HighPassCutoffHz = settings.HighPassCutoffHz;
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
                HotkeyModifierFlags = settings.HotkeyModifiers;
                HotkeyVirtualKey = settings.HotkeyVirtualKey;
                HotkeyDisplayText = BuildHotkeyDisplayText(settings.HotkeyModifiers, settings.HotkeyVirtualKey);
            }

            _publishedDriverInfName = settings.PublishedDriverInfName;
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
            GateThresholdDb = GateThresholdDb,
            DenoiserWetMix = DenoiserWetMix,
            AgcTargetLevelDb = AgcTargetLevelDb,
            CompressorThresholdDb = CompressorThresholdDb,
            CompressorRatio = CompressorRatio,
            HighPassCutoffHz = HighPassCutoffHz,
            HotkeyModifiers = HotkeyModifierFlags,
            HotkeyVirtualKey = HotkeyVirtualKey,
            LaunchOnStartup = IsAutostartEnabled,
            PublishedDriverInfName = _publishedDriverInfName,
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

    private async Task RunCalibrationAsync()
    {
        if (_engine.Pipeline == null)
        {
            StatusMessage = "Сначала выберите устройства ввода и вывода.";
            return;
        }

        // На время калибровки временно отключаем прослушивание себя: если у
        // пользователя колонки (а не наушники), обработанный звук из колонок
        // попал бы обратно в микрофон и исказил измерения фонового шума.
        bool wasMonitoring = IsMonitoring;
        if (wasMonitoring) IsMonitoring = false;

        IsCalibrating = true;
        _calibrationCts = new CancellationTokenSource();
        var calibrationEngine = new CalibrationEngine(_engine);

        var progress = new Progress<CalibrationProgressEventArgs>(p =>
        {
            CalibrationInstruction = $"{p.StepNumber}/4: {p.Instruction}";
            CalibrationProgress = p.OverallFraction * 100.0;
        });

        try
        {
            // Пресет "снимается" сразу — теперь активна калибровка, а не фиксированный пресет.
            _selectedPreset = null;
            OnPropertyChanged(nameof(SelectedPreset));
            ActivePresetLabel = "Автонастройка выполняется...";

            CalibrationResult result = await calibrationEngine.RunAsync(progress, _calibrationCts.Token, (stage, partial) =>
            {
                // Реальные, промежуточные значения применяются к ползункам сразу
                // после каждого этапа — пользователь видит, что программа
                // ДЕЙСТВИТЕЛЬНО анализирует его микрофон здесь и сейчас, а не
                // просто крутит прогресс-бар 20 секунд и подставляет числа в конце.
                _isApplyingPresetOrCalibration = true;
                try
                {
                    switch (stage)
                    {
                        case 1:
                            GateThresholdDb = partial.GateThresholdDb;
                            DenoiserWetMix = partial.DenoiserWetMix;
                            break;
                        case 2:
                            AgcTargetLevelDb = -18f;
                            break;
                        case 3:
                            CompressorThresholdDb = partial.CompressorThresholdDb;
                            CompressorRatio = partial.CompressorRatio;
                            break;
                        case 4:
                            // Этап 4 (стук по клавиатуре/мышке) мог поднять порог
                            // гейта выше того, что уже применил этап 1 — обновляем
                            // ползунок финальным значением.
                            GateThresholdDb = partial.GateThresholdDb;
                            break;
                    }
                }
                finally
                {
                    _isApplyingPresetOrCalibration = false;
                }
            });

            _calibratedGateThresholdDb = result.GateThresholdDb;
            _calibratedWetMix = result.DenoiserWetMix;
            _calibratedCompThresholdDb = result.CompressorThresholdDb;
            _calibratedCompRatio = result.CompressorRatio;
            HasCalibrationResult = true;
            ActivePresetLabel = "Автонастройка (моя калибровка)";
            ScheduleSettingsSave();

            CalibrationInstruction = "Калибровка завершена.";
            CalibrationProgress = 100;
        }
        catch (OperationCanceledException)
        {
            CalibrationInstruction = "Калибровка отменена.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Ошибка калибровки: {ex.Message}";
        }
        finally
        {
            IsCalibrating = false;
            _calibrationCts?.Dispose();
            _calibrationCts = null;

            if (wasMonitoring) IsMonitoring = true;
        }
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
