using System;
using NeuroMicrophone.Audio;
using Xunit;

namespace NeuroMicrophone.Tests;

public class NoiseGateTests
{
    [Fact]
    public void Read_SignalBelowThreshold_IsAttenuatedTowardSilence()
    {
        float amplitude = LevelMeter.DbToLinear(-60f); // ниже порога -50 dBFS по умолчанию
        float[] input = new float[48000]; // 1 секунда — достаточно для полного закрытия при Release=150мс
        for (int i = 0; i < input.Length; i++) input[i] = amplitude;

        var source = new ArraySampleProvider(input);
        var gate = new NoiseGate(source) { ThresholdDb = -50f, AttackMs = 5f, ReleaseMs = 150f };

        float[] output = new float[input.Length];
        gate.Read(output, 0, output.Length);

        float tail = output[output.Length - 1];
        Assert.True(Math.Abs(tail) < amplitude * 0.05f,
            $"Ожидалось почти полное закрытие гейта, получено значение {tail}");
    }

    [Fact]
    public void Read_SignalAboveThreshold_PassesThroughAfterAttack()
    {
        float amplitude = LevelMeter.DbToLinear(-20f); // выше порога -50 dBFS
        float[] input = new float[4800]; // 100 мс — с запасом относительно Attack=5мс

        for (int i = 0; i < input.Length; i++) input[i] = amplitude;

        var source = new ArraySampleProvider(input);
        var gate = new NoiseGate(source) { ThresholdDb = -50f, AttackMs = 5f, ReleaseMs = 150f };

        float[] output = new float[input.Length];
        gate.Read(output, 0, output.Length);

        float tail = output[output.Length - 1];
        Assert.True(Math.Abs(tail - amplitude) < amplitude * 0.05f,
            $"Ожидалось практически полное открытие гейта, получено {tail} при входе {amplitude}");
    }

    private sealed class FakeVoiceActivitySource : IVoiceActivitySource
    {
        public float VadProbability { get; set; }
        public bool IsAvailable { get; set; } = true;
    }

    [Fact]
    public void Read_QuietSignalWithHighVadProbability_StaysOpen()
    {
        // Громкость ниже порога, но VAD уверенно говорит "это речь" —
        // гейт не должен закрываться (в отличие от чистого порога по амплитуде).
        float amplitude = LevelMeter.DbToLinear(-60f);
        float[] input = new float[48000];
        for (int i = 0; i < input.Length; i++) input[i] = amplitude;

        var vad = new FakeVoiceActivitySource { VadProbability = 0.9f, IsAvailable = true };
        var source = new ArraySampleProvider(input);
        var gate = new NoiseGate(source, vad) { ThresholdDb = -50f, AttackMs = 5f, ReleaseMs = 150f, VadThreshold = 0.6f };

        float[] output = new float[input.Length];
        gate.Read(output, 0, output.Length);

        float tail = output[output.Length - 1];
        Assert.True(Math.Abs(tail - amplitude) < amplitude * 0.05f,
            $"Ожидалось, что высокая вероятность VAD удержит гейт открытым, получено {tail} при входе {amplitude}");
    }

    [Fact]
    public void Read_SignalDropsBelowThreshold_GateStaysOpenDuringHoldWindow()
    {
        const int sampleRate = 48000;
        int loudSamples = (int)(0.05 * sampleRate);  // 50 мс громкого сигнала
        int quietSamples = (int)(0.03 * sampleRate); // 30 мс тихого сигнала — меньше Hold-окна (100 мс)

        float loudAmplitude = LevelMeter.DbToLinear(-20f);  // выше порога -50 дБ
        float quietAmplitude = LevelMeter.DbToLinear(-70f); // ниже порога, но не ноль — так видно, открыт ли гейт

        float[] input = new float[loudSamples + quietSamples];
        for (int i = 0; i < loudSamples; i++) input[i] = loudAmplitude;
        for (int i = loudSamples; i < input.Length; i++) input[i] = quietAmplitude;

        var source = new ArraySampleProvider(input, sampleRate);
        var gate = new NoiseGate(source) { ThresholdDb = -50f, AttackMs = 5f, HoldMs = 100f, ReleaseMs = 150f };

        float[] output = new float[input.Length];
        gate.Read(output, 0, output.Length);

        float tail = output[output.Length - 1];

        // 30 мс тишины меньше 100-мс окна Hold — гейт должен оставаться
        // практически полностью открытым, то есть выход должен быть близок
        // к тихому входному сигналу, а не к нулю.
        Assert.True(Math.Abs(tail - quietAmplitude) < quietAmplitude * 0.3f,
            $"Ожидалось, что гейт останется открытым во время Hold-окна: вход={quietAmplitude}, выход={tail}");
    }

    [Fact]
    public void Read_SignalDropsBelowThreshold_GateClosesAfterHoldWindowExpires()
    {
        const int sampleRate = 48000;
        int loudSamples = (int)(0.05 * sampleRate); // 50 мс громкого сигнала
        int quietSamples = (int)(1.0 * sampleRate); // 1 секунда тихого сигнала — Hold(100мс) + ~900мс Release,
                                                      // что даёт экспоненциальному спаду (тау=150мс) осесть до <1%

        float loudAmplitude = LevelMeter.DbToLinear(-20f);
        float quietAmplitude = LevelMeter.DbToLinear(-70f);

        float[] input = new float[loudSamples + quietSamples];
        for (int i = 0; i < loudSamples; i++) input[i] = loudAmplitude;
        for (int i = loudSamples; i < input.Length; i++) input[i] = quietAmplitude;

        var source = new ArraySampleProvider(input, sampleRate);
        var gate = new NoiseGate(source) { ThresholdDb = -50f, AttackMs = 5f, HoldMs = 100f, ReleaseMs = 150f };

        float[] output = new float[input.Length];
        gate.Read(output, 0, output.Length);

        float tail = output[output.Length - 1];

        // К этому моменту прошли и Hold (100 мс), и ~6 постоянных времени
        // Release (150 мс каждая) — гейт должен практически полностью
        // закрыться, несмотря на то что вход всё ещё не ноль. Экспоненциальный
        // спад математически никогда не достигает точного нуля, поэтому порог
        // сравнения — не "равно нулю", а "на порядок меньше входного сигнала".
        Assert.True(Math.Abs(tail) < quietAmplitude * 0.05f,
            $"Ожидалось полное закрытие гейта после Hold+Release, получено {tail}");
    }
}
