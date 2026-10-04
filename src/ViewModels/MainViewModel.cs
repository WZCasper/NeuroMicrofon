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
    private readonly AudioEngine _engine = new();
    private readonly DriverInstaller _driverInstaller = new();

    private readonly DispatcherTimer _meterTimer;
    private readonly DispatcherTimer _saveDebounceTimer;

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

            if (Dsp.IsMonitoring && value != null)
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

    /// <summary>
    /// Этап B разбиения: работа с виртуальным драйвером вынесена в
    /// настоящий дочерний ViewModel (не просто в отдельный partial-файл, как
    /// на Этапе A). MainViewModel передаёт ему только то, что ему реально
    /// нужно извне — сообщить статус и попросить сохранить настройки —
    /// коллбэками, не раскрывая ему ничего другого о себе.
    /// </summary>
    public DriverViewModel Driver { get; }

    /// <summary>Этап B разбиения: горячая клавиша и проверка обновлений — тем же принципом, что и Driver выше.</summary>
    public HotkeyAndUpdatesViewModel HotkeyAndUpdates { get; }

    /// <summary>
    /// Этап B разбиения: DSP-цепочка — тем же принципом, что и Driver выше.
    /// Дополнительно получает общий AudioEngine (тот же экземпляр, что и
    /// Core) и способ узнать текущее устройство для прослушивания — оно
    /// выбирается на Core (SelectedMonitorDevice), поэтому передаётся
    /// отложенно, через делегат, а не значением на момент конструктора.
    /// </summary>
    public DspViewModel Dsp { get; }

    /// <summary>
    /// Этап B разбиения, финальный шаг: автонастройка — тем же принципом,
    /// что и остальные выше. В отличие от них, по-настоящему зависит от
    /// соседнего дочернего ViewModel — получает уже созданный Dsp и
    /// применяет к нему промежуточные значения по ходу калибровки только
    /// через его публичное API, см. комментарий в начале CalibrationViewModel.cs.
    /// Поэтому конструируется ПОСЛЕ Dsp.
    /// </summary>
    public CalibrationViewModel Calibration { get; }

    public MainViewModel()
    {
        Driver = new DriverViewModel(_driverInstaller, message => StatusMessage = message, ScheduleSettingsSave);
        HotkeyAndUpdates = new HotkeyAndUpdatesViewModel(message => StatusMessage = message, ScheduleSettingsSave);
        Dsp = new DspViewModel(_engine, () => SelectedMonitorDevice, message => StatusMessage = message,
            ScheduleSettingsSave, () => _isLoadingSettings);
        Calibration = new CalibrationViewModel(_engine, Dsp, message => StatusMessage = message, ScheduleSettingsSave);

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
        // Driver уже сам определил своё состояние в собственном конструкторе выше.
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
        // Проверку обновлений запускает собственный конструктор HotkeyAndUpdates выше.
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
        DspPipeline? pipeline = _engine.Pipeline;
        if (pipeline == null) return;

        pipeline.Gate.ThresholdDb = Dsp.GateThresholdDb;
        pipeline.Denoiser.WetMix = Dsp.DenoiserWetMix;
        pipeline.Agc.TargetLevelDb = Dsp.AgcTargetLevelDb;
        pipeline.Comp.ThresholdDb = Dsp.CompressorThresholdDb;
        pipeline.Comp.Ratio = Dsp.CompressorRatio;
        pipeline.HighPass.CutoffHz = Dsp.HighPassCutoffHz;

        pipeline.Denoiser.Enabled = Dsp.DenoiserEnabled;
        pipeline.Gate.Enabled = Dsp.GateEnabled;
        pipeline.Agc.Enabled = Dsp.AgcEnabled;
        pipeline.Comp.Enabled = Dsp.CompEnabled;
    }

    public void Dispose()
    {
        _meterTimer.Stop();
        _saveDebounceTimer.Stop();
        Calibration.CancelPendingCalibration();
        _engine.Dispose();
    }
}
