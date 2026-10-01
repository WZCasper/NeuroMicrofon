// MainViewModel: DSP-цепочка — пресеты, параметры-ползунки (гейт, AGC,
// компрессор, ВЧ-фильтр), включение/отключение отдельных модулей, заглушка
// микрофона. Вынесено из основного файла по мере его роста.

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

    public ICommand ToggleMuteCommand { get; }

    public ICommand SelectPresetCommand { get; }
}
