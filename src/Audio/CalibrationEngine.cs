using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NeuroMicrophone.Models;

namespace NeuroMicrophone.Audio;

/// <summary>
/// Асинхронный, неблокирующий движок автонастройки. Выполняется в фоновом
/// потоке (через Task.Delay в цикле опроса — без Thread.Sleep, не блокируя
/// UI) и последовательно проходит четыре этапа по 5 секунд (20 секунд всего):
///
///   1) фоновый шум         → порог шумового гейта, "агрессивность" RNNoise;
///   2) обычная речь         → целевой уровень AGC (-18 dBFS);
///   3) громкая/эмоц. речь    → порог/коэффициент компрессора, потолок лимитера;
///   4) стук по клавиатуре/мышке → уточнение порога шумового гейта.
///
/// Этап 4 нужен потому, что механический/ударный шум (щелчки клавиш,
/// клики мыши) обычно громче ровного фонового гула, на котором строится
/// порог гейта на этапе 1 — а правило открытия гейта в NoiseGate это
/// "громкость выше порога ИЛИ есть голос по VAD" (см. NoiseGate), то есть
/// достаточно громкий щелчок откроет гейт сам по себе, независимо от VAD,
/// раз он не про распознавание речи. Поэтому единственная защита от таких
/// щелчков — сам порог по амплитуде: он должен быть выше типичного пика
/// щелчка, но не выше настолько, чтобы начала обрезаться обычная тихая
/// речь (используется результат этапа 2 как безопасный потолок). Итоговая
/// формула вынесена в чистую статическую функцию
/// ComputeKeyboardAwareGateThreshold — её можно проверить юнит-тестом без
/// реального аудиодвижка.
///
/// Все измерения делаются по "сырому" (RawRmsDb/RawPeakDb) сигналу —
/// то есть ДО применения DSP-цепочки, чтобы результат не зависел от ещё не
/// настроенных стадий и был честным измерением реального микрофона.
/// </summary>
public sealed class CalibrationEngine
{
    private const int TotalStages = 4;
    private static readonly TimeSpan PhaseDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(50);

    private readonly AudioEngine _engine;

    public CalibrationEngine(AudioEngine engine)
    {
        _engine = engine;
    }

    public async Task<CalibrationResult> RunAsync(
        IProgress<CalibrationProgressEventArgs> progress,
        CancellationToken cancellationToken,
        Action<int, CalibrationResult>? onStageApplied = null)
    {
        var result = new CalibrationResult();

        // --- Этап 1 (0-5с): анализ фонового шума ---
        var (noiseMeanSquare, noisePeakDb) = await MeasurePhaseAsync(
            1, "Помолчите 5 секунд...", progress, cancellationToken).ConfigureAwait(true);

        float noiseFloorRmsDb = LevelMeter.LinearToDb(Math.Sqrt(noiseMeanSquare));
        result.NoiseFloorRmsDb = noiseFloorRmsDb;
        result.NoiseFloorPeakDb = noisePeakDb;
        result.GateThresholdDb = noiseFloorRmsDb + 6f;

        // Чем плотнее/громче фоновый шум, тем выше доля подавленного (wet) сигнала RNNoise.
        result.DenoiserWetMix = noiseFloorRmsDb switch
        {
            <= -60f => 0.5f,
            <= -45f => 0.75f,
            _ => 1.0f,
        };

        ApplyStageOneSettings(result);
        onStageApplied?.Invoke(1, result);

        // --- Этап 2 (5-10с): анализ обычной речи ---
        var (speechMeanSquare, _) = await MeasurePhaseAsync(
            2, "Поговорите обычным голосом...", progress, cancellationToken).ConfigureAwait(true);

        float speechAverageRmsDb = LevelMeter.LinearToDb(Math.Sqrt(speechMeanSquare));
        result.SpeechAverageRmsDb = speechAverageRmsDb;
        result.AgcTargetGainDb = -18f - speechAverageRmsDb;

        ApplyStageTwoSettings();
        onStageApplied?.Invoke(2, result);

        // --- Этап 3 (10-15с): анализ громкой/эмоциональной речи ---
        var (_, loudPeakDb) = await MeasurePhaseAsync(
            3, "Поговорите громко или посмейтесь...", progress, cancellationToken).ConfigureAwait(true);

        result.LoudSpeechPeakDb = loudPeakDb;
        result.CompressorThresholdDb = -12f;

        // Чем сильнее пик превышает порог компрессора, тем жёстче коэффициент сжатия (в пределах 3:1–4:1).
        float overshoot = loudPeakDb - result.CompressorThresholdDb;
        result.CompressorRatio = overshoot > 12f ? 4f : 3f;
        result.LimiterCeilingDb = -2f;

        ApplyStageThreeSettings(result);
        onStageApplied?.Invoke(3, result);

        // --- Этап 4 (15-20с): ударный/механический шум (клавиатура, мышь) ---
        var (_, keyboardPeakDb) = await MeasurePhaseAsync(
            4, "Постучите по клавиатуре и покликайте мышкой...", progress, cancellationToken).ConfigureAwait(true);

        result.KeyboardNoisePeakDb = keyboardPeakDb;
        result.GateThresholdDb = ComputeKeyboardAwareGateThreshold(
            baselineThresholdDb: result.GateThresholdDb,
            keyboardPeakDb: keyboardPeakDb,
            speechAverageRmsDb: result.SpeechAverageRmsDb);

        ApplyStageFourSettings(result);
        onStageApplied?.Invoke(4, result);

        return result;
    }

    /// <summary>
    /// Итоговый порог шумового гейта с учётом ударного/механического шума.
    /// Поднимает порог, полученный на этапе 1 (фоновый шум + 6 дБ), настолько,
    /// чтобы измеренный пик щелчков клавиатуры/мыши больше не открывал гейт —
    /// но не выше безопасного потолка ниже обычной речи, иначе начала бы
    /// обрезаться сама речь. Никогда не опускает порог ниже базового значения
    /// этапа 1 — в худшем случае (очень шумное окружение, где даже базовый
    /// порог уже близок к уровню речи) просто оставляет его как есть.
    ///
    /// Чистая функция без побочных эффектов — юнит-тестируется без реального
    /// аудиодвижка, в отличие от остального движка калибровки, которому для
    /// измерений нужен живой AudioEngine.
    /// </summary>
    public static float ComputeKeyboardAwareGateThreshold(
        float baselineThresholdDb, float keyboardPeakDb, float speechAverageRmsDb)
    {
        const float KeyboardMarginDb = 3f;   // запас над измеренным пиком (сам пик — уже "худший случай")
        const float SpeechSafetyMarginDb = 6f; // не поднимаем порог ближе этого к обычной речи

        float keyboardAwareThresholdDb = keyboardPeakDb + KeyboardMarginDb;
        float speechSafetyCeilingDb = speechAverageRmsDb - SpeechSafetyMarginDb;

        float candidateThresholdDb = Math.Max(baselineThresholdDb, keyboardAwareThresholdDb);
        float cappedThresholdDb = Math.Min(candidateThresholdDb, speechSafetyCeilingDb);

        return Math.Max(cappedThresholdDb, baselineThresholdDb);
    }

    private async Task<(double meanSquare, float peakDb)> MeasurePhaseAsync(
        int stepNumber,
        string instruction,
        IProgress<CalibrationProgressEventArgs> progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        double accumulatedMeanSquare = 0;
        int sampleCount = 0;
        float peakHoldDb = LevelMeter.MinDb;

        while (stopwatch.Elapsed < PhaseDuration)
        {
            cancellationToken.ThrowIfCancellationRequested();

            float rmsDb = _engine.RawRmsDb;
            float peakDb = _engine.RawPeakDb;

            double linearRms = LevelMeter.DbToLinear(rmsDb);
            accumulatedMeanSquare += linearRms * linearRms;
            sampleCount++;

            if (peakDb > peakHoldDb) peakHoldDb = peakDb;

            double phaseFraction = Math.Min(1.0, stopwatch.Elapsed.TotalSeconds / PhaseDuration.TotalSeconds);
            double overallFraction = ((stepNumber - 1) + phaseFraction) / TotalStages;

            progress.Report(new CalibrationProgressEventArgs(stepNumber, instruction, phaseFraction, overallFraction));

            await Task.Delay(SampleInterval, cancellationToken).ConfigureAwait(true);
        }

        double meanSquare = sampleCount > 0 ? accumulatedMeanSquare / sampleCount : 0;
        return (meanSquare, peakHoldDb);
    }

    private void ApplyStageOneSettings(CalibrationResult result)
    {
        DspPipeline? dsp = _engine.Pipeline;
        if (dsp == null) return;

        dsp.Gate.ThresholdDb = result.GateThresholdDb;
        dsp.Denoiser.WetMix = result.DenoiserWetMix;
    }

    private void ApplyStageTwoSettings()
    {
        DspPipeline? dsp = _engine.Pipeline;
        if (dsp == null) return;

        dsp.Agc.TargetLevelDb = -18f;
    }

    private void ApplyStageThreeSettings(CalibrationResult result)
    {
        DspPipeline? dsp = _engine.Pipeline;
        if (dsp == null) return;

        dsp.Comp.ThresholdDb = result.CompressorThresholdDb;
        dsp.Comp.Ratio = result.CompressorRatio;
        dsp.Lim.CeilingDb = result.LimiterCeilingDb;
        dsp.Lim.Enabled = true;
    }

    private void ApplyStageFourSettings(CalibrationResult result)
    {
        DspPipeline? dsp = _engine.Pipeline;
        if (dsp == null) return;

        // Порог мог измениться (подняться) по сравнению с тем, что уже
        // применил ApplyStageOneSettings — применяем финальное значение.
        dsp.Gate.ThresholdDb = result.GateThresholdDb;
    }
}
