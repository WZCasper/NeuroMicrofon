using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

    private double _compressorGainReductionDb;
    public double CompressorGainReductionDb { get => _compressorGainReductionDb; private set => SetProperty(ref _compressorGainReductionDb, value); }

    private double _limiterGainReductionDb;
    public double LimiterGainReductionDb { get => _limiterGainReductionDb; private set => SetProperty(ref _limiterGainReductionDb, value); }

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

    private DspPreset? _selectedPreset;
    public DspPreset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!SetProperty(ref _selectedPreset, value) || value == null) return;

            GateThresholdDb = value.GateThresholdDb;
            DenoiserWetMix = value.DenoiserWetMix;
            AgcTargetLevelDb = value.AgcTargetLevelDb;
            CompressorThresholdDb = value.CompressorThresholdDb;
            CompressorRatio = value.CompressorRatio;
        }
    }

    private float _gateThresholdDb = -50f;
    public float GateThresholdDb
    {
        get => _gateThresholdDb;
        set
        {
            if (!SetProperty(ref _gateThresholdDb, value)) return;
            if (_engine.Pipeline != null) _engine.Pipeline.Gate.ThresholdDb = value;
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
            ScheduleSettingsSave();
        }
    }

    public ICommand AutoTuneCommand { get; }
    public ICommand ToggleMuteCommand { get; }
    public ICommand InstallDriverCommand { get; }
    public ICommand UninstallDriverCommand { get; }

    public MainViewModel()
    {
        AutoTuneCommand = new RelayCommand(async () => await RunCalibrationAsync(), () => !IsCalibrating && _engine.IsRunning);
        ToggleMuteCommand = new RelayCommand(() => IsMuted = !IsMuted);
        InstallDriverCommand = new RelayCommand(async () => await InstallDriverAsync(), () => !IsDriverInstalled);
        UninstallDriverCommand = new RelayCommand(async () => await UninstallDriverAsync(), () => IsDriverInstalled);

        _engine.ErrorOccurred += (_, message) => StatusMessage = message;

        RefreshDeviceLists();
        IsDriverInstalled = _driverInstaller.IsDriverInstalled();
        IsAutostartEnabled = AutostartService.IsEnabled();

        SelectedInputDevice = InputDevices.FirstOrDefault();
        SelectedOutputDevice =
            OutputDevices.FirstOrDefault(d => d.Name.Contains(DriverInstaller.VirtualDeviceName, StringComparison.OrdinalIgnoreCase))
            ?? OutputDevices.FirstOrDefault();
        SelectedMonitorDevice = OutputDevices.FirstOrDefault();

        _saveDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveDebounceTimer.Tick += async (_, _) =>
        {
            _saveDebounceTimer.Stop();
            await SaveSettingsAsync();
        };

        _meterTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33), // ~30 кадров/с — достаточно для плавных индикаторов
        };
        _meterTimer.Tick += (_, _) =>
        {
            RmsLevel = _engine.ProcessedRmsDb;
            PeakLevel = _engine.ProcessedPeakDb;
            CompressorGainReductionDb = _engine.Pipeline?.Comp.CurrentGainReductionDb ?? 0.0;
            LimiterGainReductionDb = _engine.Pipeline?.Lim.CurrentGainReductionDb ?? 0.0;
        };
        _meterTimer.Start();

        _ = LoadSettingsAndApplyAsync();
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
            LaunchOnStartup = IsAutostartEnabled,
            PublishedDriverInfName = _publishedDriverInfName,
            HasDspSettings = true,
        };

        await _settingsService.SaveAsync(settings);
    }

    /// <summary>Принудительно сбрасывает отложенное сохранение — вызывается при закрытии приложения.</summary>
    public async Task FlushSettingsAsync()
    {
        _saveDebounceTimer.Stop();
        await SaveSettingsAsync();
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
            CalibrationInstruction = $"{p.StepNumber}/3: {p.Instruction}";
            CalibrationProgress = p.OverallFraction * 100.0;
        });

        try
        {
            CalibrationResult result = await calibrationEngine.RunAsync(progress, _calibrationCts.Token);

            // Синхронизируем прокси-свойства (и, соответственно, слайдеры в UI)
            // с результатом калибровки.
            GateThresholdDb = result.GateThresholdDb;
            DenoiserWetMix = result.DenoiserWetMix;
            AgcTargetLevelDb = -18f;
            CompressorThresholdDb = result.CompressorThresholdDb;
            CompressorRatio = result.CompressorRatio;

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
        DriverInstallResult result = await _driverInstaller.UninstallDriverAsync(_publishedDriverInfName, DriverInfFileName);

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
