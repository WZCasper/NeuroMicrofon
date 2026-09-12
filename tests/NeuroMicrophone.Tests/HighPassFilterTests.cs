using System;
using NeuroMicrophone.Audio;
using Xunit;

namespace NeuroMicrophone.Tests;

public class HighPassFilterTests
{
    private static float[] GenerateSine(float frequencyHz, int sampleRate, int sampleCount, float amplitude = 0.5f)
    {
        float[] data = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            data[i] = amplitude * (float)Math.Sin(2.0 * Math.PI * frequencyHz * i / sampleRate);
        }
        return data;
    }

    private static float MeasureRmsOfTail(float[] data, int tailSamples)
    {
        int start = Math.Max(0, data.Length - tailSamples);
        double sumSquares = 0;
        int count = data.Length - start;
        for (int i = start; i < data.Length; i++) sumSquares += data[i] * data[i];
        return (float)Math.Sqrt(sumSquares / count);
    }

    [Fact]
    public void Read_LowFrequencyBelowCutoff_IsStronglyAttenuated()
    {
        const int sampleRate = 48000;
        float[] input = GenerateSine(20f, sampleRate, sampleRate * 2); // 20 Гц, значительно ниже среза 90 Гц

        var source = new ArraySampleProvider(input, sampleRate);
        var filter = new HighPassFilter(source) { CutoffHz = 90f };

        float[] output = new float[input.Length];
        filter.Read(output, 0, output.Length);

        float inputRms = MeasureRmsOfTail(input, sampleRate / 2);
        float outputRms = MeasureRmsOfTail(output, sampleRate / 2);

        Assert.True(outputRms < inputRms * 0.3f,
            $"Ожидалось сильное подавление 20 Гц ниже среза 90 Гц: вход RMS={inputRms}, выход RMS={outputRms}");
    }

    [Fact]
    public void Read_FrequencyWellAboveCutoff_PassesThroughMostlyUnattenuated()
    {
        const int sampleRate = 48000;
        float[] input = GenerateSine(1000f, sampleRate, sampleRate); // 1 кГц, значительно выше среза 90 Гц

        var source = new ArraySampleProvider(input, sampleRate);
        var filter = new HighPassFilter(source) { CutoffHz = 90f };

        float[] output = new float[input.Length];
        filter.Read(output, 0, output.Length);

        float inputRms = MeasureRmsOfTail(input, sampleRate / 4);
        float outputRms = MeasureRmsOfTail(output, sampleRate / 4);

        Assert.True(outputRms > inputRms * 0.9f,
            $"Ожидалось, что 1 кГц пройдёт почти без ослабления: вход RMS={inputRms}, выход RMS={outputRms}");
    }

    [Fact]
    public void Enabled_False_PassesSignalUnchanged()
    {
        const int sampleRate = 48000;
        float[] input = GenerateSine(20f, sampleRate, 4800);

        var source = new ArraySampleProvider(input, sampleRate);
        var filter = new HighPassFilter(source) { CutoffHz = 90f, Enabled = false };

        float[] output = new float[input.Length];
        filter.Read(output, 0, output.Length);

        for (int i = 0; i < output.Length; i++)
        {
            Assert.Equal((double)input[i], (double)output[i], precision: 5);
        }
    }
}
