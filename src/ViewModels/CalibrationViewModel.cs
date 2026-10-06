// Этап B разбиения MainViewModel, финальный шаг: настоящий дочерний
// ViewModel для автонастройки (калибровки) микрофона — прогресс, инструкции
// по этапам, измеренный пик щелчков клавиатуры, сохранённый результат
// калибровки и его повторное применение.
//
// В отличие от предыдущих трёх шагов, этот класс по-настоящему зависит от
// соседнего дочернего ViewModel — DspViewModel: ход калибровки применяет
// промежуточные значения прямо к ползункам DSP-цепочки по мере готовности
// каждого из 4 этапов, а не одним действием в конце. Поэтому
// CalibrationViewModel получает уже созданный DspViewModel в конструкторе
// и работает с ним ТОЛЬКО через его публичное API (ApplyExternalValues,
// BeginExternalEdit/EndExternalEdit, SetActivePresetLabel, SelectedPreset,
// IsMonitoring) — к внутренним полям DspViewModel он доступа не имеет,
// в отличие от Этапа A, где это был один общий класс.

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

public sealed class CalibrationViewModel : ViewModelBase
{
    private readonly AudioEngine _engine;
    private readonly DspViewModel _dsp;
    private readonly Action<string?> _setStatusMessage;
    private readonly Action _scheduleSettingsSave;

    private CancellationTokenSource? _calibrationCts;

    private bool _isCalibrating;
    public bool IsCalibrating { get => _isCalibrating; private set => SetProperty(ref _isCalibrating, value); }

    private int _currentStepNumber;
    /// <summary>
    /// Номер этапа (1-4), о котором сейчас отчитывается CalibrationEngine —
    /// то же число, что уже встроено в начало CalibrationInstruction
    /// ("2/4: ..."), но отдельным int, а не распарсенное из строки: по нему
    /// список из 4 шагов в интерфейсе красит каждую строку (пройден/идёт
    /// сейчас/ещё не начат) без хрупкого разбора текста. 0 — калибровка в
    /// этой сессии ещё не запускалась.
    /// </summary>
    public int CurrentStepNumber { get => _currentStepNumber; private set => SetProperty(ref _currentStepNumber, value); }

    private double _calibrationProgress;
    public double CalibrationProgress { get => _calibrationProgress; private set => SetProperty(ref _calibrationProgress, value); }

    private string _calibrationInstruction = "Нажмите «Автонастройка», чтобы откалибровать микрофон.";
    public string CalibrationInstruction { get => _calibrationInstruction; private set => SetProperty(ref _calibrationInstruction, value); }

    // Пик щелчков клавиатуры/мыши (дБ), измеренный на 4-м этапе автонастройки.
    // null — автонастройка в этой сессии ещё не дошла до 4-го этапа: строка в
    // интерфейсе скрыта, чтобы не показывать вводящее в заблуждение "0 дБ".
    private float? _keyboardNoisePeakDb;
    public float? KeyboardNoisePeakDb
    {
        get => _keyboardNoisePeakDb;
        private set
        {
            if (!SetProperty(ref _keyboardNoisePeakDb, value)) return;
            OnPropertyChanged(nameof(HasKeyboardNoisePeak));
        }
    }

    public bool HasKeyboardNoisePeak => _keyboardNoisePeakDb.HasValue;

    private bool _hasCalibrationResult;
    public bool HasCalibrationResult { get => _hasCalibrationResult; private set => SetProperty(ref _hasCalibrationResult, value); }

    private float _calibratedGateThresholdDb;
    private float _calibratedWetMix;
    private float _calibratedCompThresholdDb;
    private float _calibratedCompRatio;

    public ICommand RecallCalibrationCommand { get; }
    public ICommand AutoTuneCommand { get; }
    public ICommand CancelCommand { get; }

    public CalibrationViewModel(AudioEngine engine, DspViewModel dsp, Action<string?> setStatusMessage, Action scheduleSettingsSave)
    {
        _engine = engine;
        _dsp = dsp;
        _setStatusMessage = setStatusMessage;
        _scheduleSettingsSave = scheduleSettingsSave;

        AutoTuneCommand = new RelayCommand(async () => await RunCalibrationAsync(), () => !IsCalibrating && _engine.IsRunning);
        RecallCalibrationCommand = new RelayCommand(RecallCalibration, () => HasCalibrationResult);
        CancelCommand = new RelayCommand(CancelCalibration, () => IsCalibrating);
    }

    /// <summary>
    /// Кнопка "Отмена" в окне автонастройки. Только запрашивает отмену —
    /// освобождение токена остаётся за блоком finally в RunCalibrationAsync,
    /// поэтому здесь нет гонки "Cancel против Dispose".
    /// </summary>
    private void CancelCalibration()
    {
        try
        {
            _calibrationCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Калибровка как раз завершилась и освободила токен — отменять уже нечего.
        }
    }

    /// <summary>
    /// Вызывается при отмене/ошибке калибровки. CurrentStepNumber на этот
    /// момент указывает на этап, который только НАЧАЛСЯ (его колбэк мог ни
    /// разу не сработать) — без этой поправки список из 4 шагов в
    /// интерфейсе показал бы этот прерванный этап как "пройден" (зелёная
    /// галочка), хотя на самом деле он не завершился. Уменьшаем на 1, чтобы
    /// пройденными считались только ДЕЙСТВИТЕЛЬНО завершённые этапы.
    /// </summary>
    private void RevertInterruptedStep()
    {
        if (CurrentStepNumber > 0) CurrentStepNumber -= 1;
    }

    private void RecallCalibration()
    {
        if (!HasCalibrationResult) return;

        // Сбрасываем визуальный выбор карточки пресета — активна "своя" калибровка, а не один из фиксированных пресетов.
        _dsp.SelectedPreset = null;

        _dsp.ApplyExternalValues(_calibratedGateThresholdDb, _calibratedWetMix, -18f,
            _calibratedCompThresholdDb, _calibratedCompRatio, "Автонастройка (моя калибровка)");
    }

    private async Task RunCalibrationAsync()
    {
        if (_engine.Pipeline == null)
        {
            _setStatusMessage("Сначала выберите устройства ввода и вывода.");
            return;
        }

        // На время калибровки временно отключаем прослушивание себя: если у
        // пользователя колонки (а не наушники), обработанный звук из колонок
        // попал бы обратно в микрофон и исказил измерения фонового шума.
        bool wasMonitoring = _dsp.IsMonitoring;
        if (wasMonitoring) _dsp.IsMonitoring = false;

        IsCalibrating = true;
        // Сбрасываем до начала первого этапа — иначе при повторном запуске
        // список шагов на мгновение показал бы все 4 строки "пройдено"
        // (оставшиеся от прошлого успешного прогона) ещё до первого отчёта
        // нового CalibrationEngine.
        CurrentStepNumber = 0;
        _calibrationCts = new CancellationTokenSource();
        var calibrationEngine = new CalibrationEngine(_engine);

        var progress = new Progress<CalibrationProgressEventArgs>(p =>
        {
            CalibrationInstruction = $"{p.StepNumber}/4: {p.Instruction}";
            CalibrationProgress = p.OverallFraction * 100.0;
            CurrentStepNumber = p.StepNumber;
        });

        try
        {
            // Пресет "снимается" сразу — теперь активна калибровка, а не фиксированный пресет.
            _dsp.SelectedPreset = null;
            _dsp.SetActivePresetLabel("Автонастройка выполняется...");

            CalibrationResult result = await calibrationEngine.RunAsync(progress, _calibrationCts.Token, (stage, partial) =>
            {
                // Реальные, промежуточные значения применяются к ползункам сразу
                // после каждого этапа — пользователь видит, что программа
                // ДЕЙСТВИТЕЛЬНО анализирует его микрофон здесь и сейчас, а не
                // просто крутит прогресс-бар 20 секунд и подставляет числа в конце.
                _dsp.BeginExternalEdit();
                try
                {
                    switch (stage)
                    {
                        case 1:
                            _dsp.GateThresholdDb = partial.GateThresholdDb;
                            _dsp.DenoiserWetMix = partial.DenoiserWetMix;
                            break;
                        case 2:
                            _dsp.AgcTargetLevelDb = -18f;
                            break;
                        case 3:
                            _dsp.CompressorThresholdDb = partial.CompressorThresholdDb;
                            _dsp.CompressorRatio = partial.CompressorRatio;
                            break;
                        case 4:
                            // Этап 4 (стук по клавиатуре/мышке) мог поднять порог
                            // гейта выше того, что уже применил этап 1 — обновляем
                            // ползунок финальным значением.
                            _dsp.GateThresholdDb = partial.GateThresholdDb;

                            // Измеренный на этом этапе пик щелчков показываем рядом
                            // с прогрессом калибровки — пользователь видит, от какого
                            // уровня отталкивался новый порог гейта.
                            KeyboardNoisePeakDb = partial.KeyboardNoisePeakDb;
                            break;
                    }
                }
                finally
                {
                    _dsp.EndExternalEdit();
                }
            });

            _calibratedGateThresholdDb = result.GateThresholdDb;
            _calibratedWetMix = result.DenoiserWetMix;
            _calibratedCompThresholdDb = result.CompressorThresholdDb;
            _calibratedCompRatio = result.CompressorRatio;
            HasCalibrationResult = true;
            _dsp.SetActivePresetLabel("Автонастройка (моя калибровка)");
            _scheduleSettingsSave();

            CalibrationInstruction = "Калибровка завершена.";
            CalibrationProgress = 100;
        }
        catch (OperationCanceledException)
        {
            CalibrationInstruction = "Калибровка отменена.";

            // К моменту отмены часть ползунков уже получила значения завершённых
            // этапов, а подпись всё ещё говорила бы "Автонастройка выполняется...".
            // Честная подпись: значения теперь не соответствуют ни пресету, ни калибровке.
            _dsp.SetActivePresetLabel("Пользовательские настройки");
            RevertInterruptedStep();
        }
        catch (Exception ex)
        {
            _setStatusMessage($"Ошибка калибровки: {ex.Message}");
            RevertInterruptedStep();
        }
        finally
        {
            IsCalibrating = false;
            _calibrationCts?.Dispose();
            _calibrationCts = null;

            if (wasMonitoring) _dsp.IsMonitoring = true;
        }
    }

    /// <summary>
    /// Вызывается из MainViewModel.LoadSettingsAndApplyAsync при загрузке
    /// ранее сохранённого результата калибровки — на момент создания этого
    /// класса настройки ещё не загружены (загрузка асинхронная и идёт уже
    /// после конструктора), поэтому значения приходят отдельным вызовом.
    /// Намеренно не трогает DSP-цепочку — как и раньше, загруженный
    /// результат применяется, только если пользователь сам нажмёт "вспомнить
    /// мою калибровку" (RecallCalibrationCommand).
    /// </summary>
    public void LoadCalibrationResult(float gateThresholdDb, float wetMix, float compThresholdDb, float compRatio)
    {
        _calibratedGateThresholdDb = gateThresholdDb;
        _calibratedWetMix = wetMix;
        _calibratedCompThresholdDb = compThresholdDb;
        _calibratedCompRatio = compRatio;
        HasCalibrationResult = true;
    }

    // Для MainViewModel.Settings.cs (сохранение результата калибровки в файл настроек).
    public float CalibratedGateThresholdDb => _calibratedGateThresholdDb;
    public float CalibratedWetMix => _calibratedWetMix;
    public float CalibratedCompThresholdDb => _calibratedCompThresholdDb;
    public float CalibratedCompRatio => _calibratedCompRatio;

    /// <summary>Вызывается из MainViewModel.Dispose() при закрытии приложения — отменяет и освобождает идущую калибровку, если она есть.</summary>
    public void CancelPendingCalibration()
    {
        _calibrationCts?.Cancel();
        _calibrationCts?.Dispose();
    }
}
