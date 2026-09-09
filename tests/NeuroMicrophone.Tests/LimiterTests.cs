using System;
using NeuroMicrophone.Audio;
using Xunit;

namespace NeuroMicrophone.Tests;

public class LimiterTests
{
    private const int LookaheadFrames = 240; // соответствует lookaheadMs=5 (значение по умолчанию) при 48 кГц

    [Fact]
    public void Read_NeverExceedsCeiling()
    {
        // Сигнал существенно громче потолка лимитера (амплитуда 2.0 против шкалы [-1;1]).
        float[] input = new float[4800]; // 100 мс при 48 кГц
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (i % 2 == 0) ? 2.0f : -2.0f;
        }

        var source = new ArraySampleProvider(input);
        var limiter = new Limiter(source) { CeilingDb = -2f };

        float ceilingLinear = LevelMeter.DbToLinear(-2f);
        float[] output = new float[input.Length];
        int totalRead = 0;
        int chunk;
        while (totalRead < output.Length && (chunk = limiter.Read(output, totalRead, output.Length - totalRead)) > 0)
        {
            totalRead += chunk;
        }

        for (int i = 0; i < totalRead; i++)
        {
            Assert.True(Math.Abs(output[i]) <= ceilingLinear + 0.0001f,
                $"Сэмпл {i} превысил потолок лимитера: {output[i]}");
        }
    }

    [Fact]
    public void Read_QuietSignal_PassesThroughUnattenuatedAfterLookaheadWarmup()
    {
        int totalSamples = LookaheadFrames + 480;
        float[] input = new float[totalSamples];
        for (int i = 0; i < input.Length; i++) input[i] = 0.05f; // значительно тише потолка -2 dBFS

        var source = new ArraySampleProvider(input);
        var limiter = new Limiter(source) { CeilingDb = -2f };

        float[] output = new float[input.Length];
        limiter.Read(output, 0, output.Length);

        // Первые LookaheadFrames сэмплов — это "прогрев" линии задержки
        // лимитера (изначально заполнена нулями), поэтому проверяем только
        // сэмплы после неё.
        for (int i = LookaheadFrames; i < output.Length; i++)
        {
            Assert.Equal(0.05, (double)output[i], precision: 3);
        }
    }

    [Fact]
    public void CurrentGainReductionDb_IsZero_WhenSignalBelowCeiling()
    {
        float[] input = new float[4800];
        for (int i = 0; i < input.Length; i++) input[i] = 0.05f;

        var source = new ArraySampleProvider(input);
        var limiter = new Limiter(source) { CeilingDb = -2f };

        float[] output = new float[input.Length];
        limiter.Read(output, 0, output.Length);

        Assert.True(limiter.CurrentGainReductionDb > -0.1f,
            $"Ожидалось отсутствие снижения усиления, получено {limiter.CurrentGainReductionDb} дБ");
    }
}
