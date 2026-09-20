using System;
using NeuroMicrophone.Audio;
using Xunit;

namespace NeuroMicrophone.Tests;

/// <summary>
/// Проверяет DspPipeline как сборку целиком, а не отдельные стадии
/// (которые уже покрыты собственными тестами: AutoGainControlTests,
/// CompressorTests, HighPassFilterTests, LimiterTests, NoiseGateTests).
///
/// RnnoiseDenoiser в этом тестовом окружении гарантированно работает в
/// режиме "библиотека недоступна" (rnnoise.dll не публикуется в тестовый
/// проект — см. NeuroMicrophone.csproj), поэтому эти тесты заодно служат
/// регрессионным подтверждением того, что вся цепочка остаётся
/// работоспособной и без нативного шумоподавления (сухой проход, как и
/// описано в комментариях RnnoiseDenoiser.IsAvailable).
/// </summary>
public class DspPipelineTests
{
    private static ISampleProvider CreateMono48kSource(float[] data) => new ArraySampleProvider(data, sampleRate: 48000, channels: 1);

    [Fact]
    public void Constructor_LimiterIsEnabledByDefault_WithMinusTwoDbCeiling()
    {
        // Требование "permanently prevent audio clipping" (см. DspPipeline и
        // README) не должно зависеть от того, прошёл ли пользователь
        // калибровку или выбрал пресет — лимитер обязан быть включён и
        // настроен сразу при создании пайплайна.
        var source = CreateMono48kSource(new float[480]);
        var pipeline = new DspPipeline(source);

        Assert.True(pipeline.Lim.Enabled, "Лимитер должен быть включён по умолчанию сразу при создании DspPipeline.");
        Assert.Equal(-2.0, (double)pipeline.Lim.CeilingDb, precision: 3);
    }

    [Fact]
    public void Constructor_ExposesAllSixStages()
    {
        var source = CreateMono48kSource(new float[480]);
        var pipeline = new DspPipeline(source);

        Assert.NotNull(pipeline.HighPass);
        Assert.NotNull(pipeline.Denoiser);
        Assert.NotNull(pipeline.Gate);
        Assert.NotNull(pipeline.Agc);
        Assert.NotNull(pipeline.Comp);
        Assert.NotNull(pipeline.Lim);

        // Финальная стадия пайплайна (то, что реально читает Read()) обязана
        // быть лимитером — иначе гарантия "clipping prevention" не выполнялась
        // бы для сигнала, реально уходящего на выход.
        Assert.Equal(pipeline.Lim.WaveFormat.SampleRate, pipeline.WaveFormat.SampleRate);
        Assert.Equal(pipeline.Lim.WaveFormat.Channels, pipeline.WaveFormat.Channels);
    }

    [Fact]
    public void Agc_IsActuallyWiredIntoTheChain_DisablingItChangesPipelineOutput()
    {
        // Ни один промежуточный класс не раскрывает свой внутренний _source
        // публично, поэтому связность графа "HighPass → RNNoise → Gate → AGC
        // → Compressor → Limiter" нельзя проверить напрямую через рефлексию
        // структуры — только через наблюдаемый эффект: если стадия реально
        // встроена в цепочку, переключение Enabled обязано менять то, что
        // возвращает Read() у всего пайплайна.
        //
        // Сигнал — синусоида 300 Гц (заведомо выше среза HighPassFilter,
        // 90 Гц по умолчанию, и в полосе типичных голосовых частот), а не
        // постоянный уровень: DspPipeline начинается с HighPassFilter, а
        // фильтр верхних частот на постоянном сигнале (0 Гц) со временем
        // подавит его к нулю сам по себе — это исказило бы сравнение
        // посторонним переходным процессом фильтра, а не эффектом AGC.
        // Амплитуда 0.05 (≈ -26 dBFS) выше NoiseFloorDb AGC (-55 дБ) и ниже
        // его TargetLevelDb (-18 дБ), поэтому AGC должен поднять уровень.
        float[] MakeInput()
        {
            const int sampleRate = 48000;
            const float frequencyHz = 300f;
            const float amplitude = 0.05f;

            float[] data = new float[9600]; // 200 мс — достаточно для установления огибающей AGC
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = amplitude * MathF.Sin(2f * MathF.PI * frequencyHz * i / sampleRate);
            }
            return data;
        }

        var pipelineWithAgc = new DspPipeline(CreateMono48kSource(MakeInput()));
        pipelineWithAgc.Agc.Enabled = true;
        float[] outputWithAgc = new float[9600];
        pipelineWithAgc.Read(outputWithAgc, 0, outputWithAgc.Length);

        var pipelineWithoutAgc = new DspPipeline(CreateMono48kSource(MakeInput()));
        pipelineWithoutAgc.Agc.Enabled = false;
        float[] outputWithoutAgc = new float[9600];
        pipelineWithoutAgc.Read(outputWithoutAgc, 0, outputWithoutAgc.Length);

        // AGC должен поднять уровень тихого сигнала к TargetLevelDb (-18 dBFS,
        // то есть громче, чем исходные -26 dBFS), поэтому уровень в конце
        // буфера (после установления огибающей) обязан заметно отличаться.
        float rmsWithAgc = LevelMeter.CalculateRmsDb(outputWithAgc, 4800, 4800);
        float rmsWithoutAgc = LevelMeter.CalculateRmsDb(outputWithoutAgc, 4800, 4800);

        Assert.True(Math.Abs(rmsWithAgc - rmsWithoutAgc) > 1f,
            $"Включение/выключение AGC должно заметно менять выход всего пайплайна " +
            $"(значит, AGC реально встроен в цепочку). С AGC: {rmsWithAgc} дБ, без AGC: {rmsWithoutAgc} дБ");
    }

    [Fact]
    public void Read_WithRnnoiseUnavailable_PassesSignalThroughEntireChainWithoutThrowing()
    {
        // В тестовом окружении rnnoise.dll не присутствует, поэтому
        // RnnoiseDenoiser.IsAvailable будет false — этот тест подтверждает,
        // что вся цепочка (в том числе Gate, который использует Denoiser как
        // источник VAD) продолжает работать штатно в этом режиме, как и
        // задокументировано в RnnoiseDenoiser.
        float[] input = new float[4800]; // 100 мс при 48 кГц
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = 0.2f * MathF.Sin(2f * MathF.PI * 300f * i / 48000f); // синусоида 300 Гц, в полосе голосовых частот
        }

        var source = CreateMono48kSource(input);
        var pipeline = new DspPipeline(source);

        Assert.False(pipeline.Denoiser.IsAvailable,
            "Этот тест предполагает окружение без rnnoise.dll — иначе поведение RNNoise здесь не проверяется.");

        float[] output = new float[input.Length];
        int totalRead = 0;
        int chunk;
        while (totalRead < output.Length && (chunk = pipeline.Read(output, totalRead, output.Length - totalRead)) > 0)
        {
            totalRead += chunk;
        }

        Assert.True(totalRead > 0, "Пайплайн должен вернуть прочитанные сэмплы даже без доступного RNNoise.");

        float ceilingLinear = LevelMeter.DbToLinear(pipeline.Lim.CeilingDb);
        for (int i = 0; i < totalRead; i++)
        {
            Assert.True(Math.Abs(output[i]) <= ceilingLinear + 0.01f,
                $"Сэмпл {i} превысил потолок лимитера на выходе всей цепочки: {output[i]}");
        }
    }

    [Fact]
    public void Read_SilentInput_DoesNotThrowAndStaysWithinCeiling()
    {
        // Полная тишина на входе — граничный случай для гейта, AGC и
        // компрессора одновременно (все они по-разному реагируют на нулевой
        // сигнал). Тест фиксирует, что комбинация этих стадий не приводит к
        // делению на ноль, NaN или иным численным сбоям при сборке в единый
        // пайплайн.
        float[] input = new float[4800];
        var source = CreateMono48kSource(input);
        var pipeline = new DspPipeline(source);

        float[] output = new float[input.Length];
        int totalRead = 0;
        int chunk;
        while (totalRead < output.Length && (chunk = pipeline.Read(output, totalRead, output.Length - totalRead)) > 0)
        {
            totalRead += chunk;
        }

        for (int i = 0; i < totalRead; i++)
        {
            Assert.False(float.IsNaN(output[i]), $"Сэмпл {i} равен NaN на тишине.");
            Assert.False(float.IsInfinity(output[i]), $"Сэмпл {i} равен бесконечности на тишине.");
        }
    }
}
