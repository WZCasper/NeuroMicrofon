using NeuroMicrophone.Audio;
using Xunit;

namespace NeuroMicrophone.Tests;

/// <summary>
/// CalibrationEngine в основном привязан к живому AudioEngine (реальным
/// аудиоустройствам) и потому не тестируется целиком юнит-тестами — но
/// формула уточнения порога гейта по этапу 4 (стук по клавиатуре/мышке)
/// вынесена в чистую статическую функцию именно для того, чтобы её можно
/// было проверить здесь, без реального аудиодвижка.
/// </summary>
public class CalibrationEngineTests
{
    [Fact]
    public void ComputeKeyboardAwareGateThreshold_QuietKeyboardNoise_DoesNotRaiseThreshold()
    {
        // Клик клавиатуры тише, чем уже установленный по фоновому шуму порог
        // (даже с запасом в 3 дБ) — порог не должен меняться.
        float result = CalibrationEngine.ComputeKeyboardAwareGateThreshold(
            baselineThresholdDb: -44f,
            keyboardPeakDb: -50f,
            speechAverageRmsDb: -25f);

        Assert.Equal(-44.0, (double)result, 3);
    }

    [Fact]
    public void ComputeKeyboardAwareGateThreshold_LoudKeyboardNoise_RaisesThresholdAboveBaseline()
    {
        // Громкий щелчок клавиатуры (-30 дБ) громче базового порога (-44 дБ),
        // но у обычной речи (-15 дБ) достаточно запаса — порог должен
        // подняться до keyboardPeak + 3 дБ = -27 дБ.
        float result = CalibrationEngine.ComputeKeyboardAwareGateThreshold(
            baselineThresholdDb: -44f,
            keyboardPeakDb: -30f,
            speechAverageRmsDb: -15f);

        Assert.Equal(-27.0, (double)result, 3);
    }

    [Fact]
    public void ComputeKeyboardAwareGateThreshold_VeryLoudKeyboardNoise_IsCappedBySpeechSafetyCeiling()
    {
        // Экстремально громкий щелчок (-10 дБ) потребовал бы порог -7 дБ,
        // но при тихой обычной речи (-25 дБ) это обрезало бы саму речь —
        // порог должен быть ограничен безопасным потолком speech - 6 дБ = -31 дБ,
        // а не поднят до уровня самого щелчка.
        float result = CalibrationEngine.ComputeKeyboardAwareGateThreshold(
            baselineThresholdDb: -44f,
            keyboardPeakDb: -10f,
            speechAverageRmsDb: -25f);

        Assert.Equal(-31.0, (double)result, 3);
    }

    [Fact]
    public void ComputeKeyboardAwareGateThreshold_PathologicalCase_NeverDropsBelowBaseline()
    {
        // Патологический случай: даже безопасный потолок (speech - 6 дБ = -28 дБ)
        // оказался ниже базового порога этапа 1 (-20 дБ) — например, очень
        // шумное окружение с тихой речью. Итоговый порог не должен ОПУСКАТЬСЯ
        // ниже базового значения, даже если формула потолка это "предлагает".
        float result = CalibrationEngine.ComputeKeyboardAwareGateThreshold(
            baselineThresholdDb: -20f,
            keyboardPeakDb: -15f,
            speechAverageRmsDb: -22f);

        Assert.Equal(-20.0, (double)result, 3);
    }

    [Fact]
    public void ComputeKeyboardAwareGateThreshold_NoKeyboardNoiseMeasured_KeepsBaselineThreshold()
    {
        // Если пик по клавиатуре оказался тише самого базового порога,
        // результат должен в точности совпасть с базовым порогом.
        float result = CalibrationEngine.ComputeKeyboardAwareGateThreshold(
            baselineThresholdDb: -40f,
            keyboardPeakDb: -60f,
            speechAverageRmsDb: -20f);

        Assert.Equal(-40.0, (double)result, 3);
    }
}
