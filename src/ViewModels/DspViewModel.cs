// Этап B разбиения MainViewModel: настоящий дочерний ViewModel для DSP-цепочки
// — пресеты, параметры-ползунки (гейт, AGC, компрессор, ВЧ-фильтр),
// включение/отключение отдельных модулей обработки, заглушка микрофона и
// прослушивание (monitoring).
//
// В отличие от Этапа A (где всё это было просто ещё одним файлом того же
// класса MainViewModel), это отдельный объект. Он получает извне только то,
// что ему реально нужно: общий AudioEngine (тот же экземпляр, что держит
// MainViewModel), откуда взять устройство для прослушивания, и два коллбэка
// родителя (сообщить статус, попросить сохранить настройки). Применение
// результата автонастройки (CalibrationViewModel) и загрузка сохранённых
// настроек (MainViewModel.Settings.cs) идут через один и тот же публичный
// метод ApplyExternalValues — он же использует SelectedPreset.

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

public sealed class DspViewModel : ViewModelBase
{
    private readonly AudioEngine _engine;
    private readonly Func<AudioDeviceInfo?> _getMonitorDevice;
    private readonly Action<string?> _setStatusMessage;
    private readonly Action _scheduleSettingsSave;
    private readonly Func<bool> _isLoadingSettings;

    public IReadOnlyList<DspPreset> Presets => DspPreset.BuiltIn;

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
                AudioDeviceInfo? monitorDevice = _getMonitorDevice();
                if (monitorDevice == null)
                {
                    _setStatusMessage("Выберите устройство для прослушивания.");
                    _isMonitoring = false;
                    OnPropertyChanged(nameof(IsMonitoring));
                    return;
                }

                try
                {
                    _engine.StartMonitoring(monitorDevice.Id);
                }
                catch (Exception ex)
                {
                    _setStatusMessage($"Не удалось включить прослушивание: {ex.Message}");
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

    // --- Расширенные настройки DSP: прокси-свойства поверх текущего DspPipeline ---

    // true только на время программного применения пресета/калибровки —
    // отличает эти изменения от ручного перетаскивания ползунка пользователем,
    // чтобы правильно показывать ActivePresetLabel ("Пользовательские
    // настройки" появляется, только если ползунок подвинул сам пользователь).
    private bool _isApplyingPresetOrCalibration;

    private string _activePresetLabel = "Пользовательские настройки";
    public string ActivePresetLabel { get => _activePresetLabel; private set => SetProperty(ref _activePresetLabel, value); }

    private DspPreset? _selectedPreset;
    public DspPreset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!SetProperty(ref _selectedPreset, value) || value == null) return;
            ApplyExternalValues(value.GateThresholdDb, value.DenoiserWetMix, value.AgcTargetLevelDb,
                value.CompressorThresholdDb, value.CompressorRatio, value.Name);
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
            MarkCustomizedIfUserEdited();
            _scheduleSettingsSave();
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
            _scheduleSettingsSave();
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
            _scheduleSettingsSave();
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
            _scheduleSettingsSave();
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
            _scheduleSettingsSave();
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
            _scheduleSettingsSave();
        }
    }

    public ICommand ToggleMuteCommand { get; }
    public ICommand SelectPresetCommand { get; }

    public DspViewModel(AudioEngine engine, Func<AudioDeviceInfo?> getMonitorDevice, Action<string?> setStatusMessage,
        Action scheduleSettingsSave, Func<bool> isLoadingSettings)
    {
        _engine = engine;
        _getMonitorDevice = getMonitorDevice;
        _setStatusMessage = setStatusMessage;
        _scheduleSettingsSave = scheduleSettingsSave;
        _isLoadingSettings = isLoadingSettings;

        ToggleMuteCommand = new RelayCommand(() => IsMuted = !IsMuted);
        SelectPresetCommand = new RelayCommand<DspPreset>(preset =>
        {
            if (preset != null) SelectedPreset = preset;
        });
    }

    /// <summary>
    /// Применяет готовый набор значений (пресет или результат калибровки) ко
    /// всей DSP-цепочке одним действием — используется SelectedPreset выше и
    /// CalibrationViewModel при повторном применении сохранённого результата
    /// калибровки (RecallCalibration), когда известны сразу все 5 значений.
    /// Отличается от ручного перетаскивания ползунка тем, что не помечает
    /// ActivePresetLabel как "Пользовательские настройки" (BeginExternalEdit/
    /// EndExternalEdit ниже на время применения), а выставляет его в
    /// activePresetLabel явно.
    /// </summary>
    public void ApplyExternalValues(float gateThresholdDb, float denoiserWetMix, float agcTargetLevelDb,
        float compressorThresholdDb, float compressorRatio, string activePresetLabel)
    {
        BeginExternalEdit();
        try
        {
            GateThresholdDb = gateThresholdDb;
            DenoiserWetMix = denoiserWetMix;
            AgcTargetLevelDb = agcTargetLevelDb;
            CompressorThresholdDb = compressorThresholdDb;
            CompressorRatio = compressorRatio;
        }
        finally
        {
            EndExternalEdit();
        }

        SetActivePresetLabel(activePresetLabel);
    }

    /// <summary>
    /// Пара методов для CalibrationViewModel: ход автонастройки применяет
    /// значения ПОСТЕПЕННО, по мере готовности каждого этапа (не все 5 сразу,
    /// как ApplyExternalValues выше), поэтому ему нужен более гранулярный
    /// контроль — самому выставить нужные свойства между Begin и End,
    /// по-прежнему не помечая это как "Пользовательские настройки".
    /// </summary>
    public void BeginExternalEdit() => _isApplyingPresetOrCalibration = true;

    public void EndExternalEdit() => _isApplyingPresetOrCalibration = false;

    /// <summary>
    /// Публичный сеттер ActivePresetLabel для CalibrationViewModel — сам
    /// autoproperty-сеттер приватный, т.к. из XAML и остальной DSP-логики
    /// label выставляется только изнутри этого класса.
    /// </summary>
    public void SetActivePresetLabel(string label) => ActivePresetLabel = label;

    /// <summary>
    /// Помечает текущий набор настроек как "пользовательский" — но только
    /// если изменение действительно пришло от пользователя (перетаскивание
    /// ползунка), а не от применения пресета/калибровки/загрузки настроек.
    /// </summary>
    private void MarkCustomizedIfUserEdited()
    {
        if (_isApplyingPresetOrCalibration || _isLoadingSettings()) return;
        ActivePresetLabel = "Пользовательские настройки";
    }
}
