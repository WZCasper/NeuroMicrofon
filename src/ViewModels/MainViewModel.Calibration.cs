// MainViewModel: состояние и ход автонастройки (калибровки) микрофона —
// прогресс, инструкции по этапам, измеренный пик щелчков клавиатуры,
// сохранённый результат калибровки и его повторное применение. Вынесено из
// основного файла по мере его роста.

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
    private CancellationTokenSource? _calibrationCts;

    private bool _isCalibrating;
    public bool IsCalibrating { get => _isCalibrating; private set => SetProperty(ref _isCalibrating, value); }

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

    private void RecallCalibration()
    {
        if (!HasCalibrationResult) return;

        // Сбрасываем визуальный выбор карточки пресета — активна "своя" калибровка, а не один из фиксированных пресетов.
        Dsp.SelectedPreset = null;

        Dsp.ApplyExternalValues(_calibratedGateThresholdDb, _calibratedWetMix, -18f,
            _calibratedCompThresholdDb, _calibratedCompRatio, "Автонастройка (моя калибровка)");
    }

    public ICommand AutoTuneCommand { get; }

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
        bool wasMonitoring = Dsp.IsMonitoring;
        if (wasMonitoring) Dsp.IsMonitoring = false;

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
            Dsp.SelectedPreset = null;
            Dsp.SetActivePresetLabel("Автонастройка выполняется...");

            CalibrationResult result = await calibrationEngine.RunAsync(progress, _calibrationCts.Token, (stage, partial) =>
            {
                // Реальные, промежуточные значения применяются к ползункам сразу
                // после каждого этапа — пользователь видит, что программа
                // ДЕЙСТВИТЕЛЬНО анализирует его микрофон здесь и сейчас, а не
                // просто крутит прогресс-бар 20 секунд и подставляет числа в конце.
                Dsp.BeginExternalEdit();
                try
                {
                    switch (stage)
                    {
                        case 1:
                            Dsp.GateThresholdDb = partial.GateThresholdDb;
                            Dsp.DenoiserWetMix = partial.DenoiserWetMix;
                            break;
                        case 2:
                            Dsp.AgcTargetLevelDb = -18f;
                            break;
                        case 3:
                            Dsp.CompressorThresholdDb = partial.CompressorThresholdDb;
                            Dsp.CompressorRatio = partial.CompressorRatio;
                            break;
                        case 4:
                            // Этап 4 (стук по клавиатуре/мышке) мог поднять порог
                            // гейта выше того, что уже применил этап 1 — обновляем
                            // ползунок финальным значением.
                            Dsp.GateThresholdDb = partial.GateThresholdDb;

                            // Измеренный на этом этапе пик щелчков показываем рядом
                            // с прогрессом калибровки — пользователь видит, от какого
                            // уровня отталкивался новый порог гейта.
                            KeyboardNoisePeakDb = partial.KeyboardNoisePeakDb;
                            break;
                    }
                }
                finally
                {
                    Dsp.EndExternalEdit();
                }
            });

            _calibratedGateThresholdDb = result.GateThresholdDb;
            _calibratedWetMix = result.DenoiserWetMix;
            _calibratedCompThresholdDb = result.CompressorThresholdDb;
            _calibratedCompRatio = result.CompressorRatio;
            HasCalibrationResult = true;
            Dsp.SetActivePresetLabel("Автонастройка (моя калибровка)");
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

            if (wasMonitoring) Dsp.IsMonitoring = true;
        }
    }
}
