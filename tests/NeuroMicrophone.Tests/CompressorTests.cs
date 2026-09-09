using System;
using NeuroMicrophone.Audio;
using Xunit;

namespace NeuroMicrophone.Tests;

public class CompressorTests
{
    [Fact]
    public void Read_SignalBelowThreshold_NoGainReduction()
    {
        float[] input = new float[4800];
        for (int i = 0; i < input.Length; i++) input[i] = 0.05f; // около -26 dBFS, ниже порога -12 dBFS

        var source = new ArraySampleProvider(input);
        var compressor = new Compressor(source) { ThresholdDb = -12f, Ratio = 4f, KneeDb = 0f };

        float[] output = new float[input.Length];
        compressor.Read(output, 0, output.Length);

        Assert.True(Math.Abs(compressor.CurrentGainReductionDb) < 0.5f,
            $"Ожидалось отсутствие сжатия ниже порога, получено {compressor.CurrentGainReductionDb} дБ");
    }

    [Fact]
    public void Read_SignalAboveThreshold_AppliesGainReduction()
    {
        float[] input = new float[9600]; // 200 мс — достаточно для установления огибающей
        for (int i = 0; i < input.Length; i++) input[i] = 0.9f; // около -0.9 dBFS, выше порога -12 dBFS

        var source = new ArraySampleProvider(input);
        var compressor = new Compressor(source)
        {
            ThresholdDb = -12f,
            Ratio = 4f,
            KneeDb = 0f,
            AttackMs = 1f,
            ReleaseMs = 50f,
        };

        float[] output = new float[input.Length];
        compressor.Read(output, 0, output.Length);

        Assert.True(compressor.CurrentGainReductionDb < -1f,
            $"Ожидалось заметное снижение усиления выше порога, получено {compressor.CurrentGainReductionDb} дБ");
    }

    [Fact]
    public void Read_HigherRatio_ProducesMoreGainReductionThanLowerRatio()
    {
        float[] MakeInput()
        {
            float[] data = new float[9600];
            for (int i = 0; i < data.Length; i++) data[i] = 0.9f;
            return data;
        }

        var lowRatioCompressor = new Compressor(new ArraySampleProvider(MakeInput()))
        {
            ThresholdDb = -12f,
            Ratio = 2f,
            KneeDb = 0f,
            AttackMs = 1f,
            ReleaseMs = 50f,
        };
        var highRatioCompressor = new Compressor(new ArraySampleProvider(MakeInput()))
        {
            ThresholdDb = -12f,
            Ratio = 8f,
            KneeDb = 0f,
            AttackMs = 1f,
            ReleaseMs = 50f,
        };

        float[] buffer = new float[9600];
        lowRatioCompressor.Read(buffer, 0, buffer.Length);
        highRatioCompressor.Read(buffer, 0, buffer.Length);

        Assert.True(highRatioCompressor.CurrentGainReductionDb < lowRatioCompressor.CurrentGainReductionDb,
            $"Ожидалось, что коэффициент 8:1 сожмёт сильнее, чем 2:1. " +
            $"Получено: 2:1 → {lowRatioCompressor.CurrentGainReductionDb} дБ, 8:1 → {highRatioCompressor.CurrentGainReductionDb} дБ");
    }
}
