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
}
