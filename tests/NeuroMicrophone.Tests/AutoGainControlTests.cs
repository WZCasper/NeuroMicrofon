using NeuroMicrophone.Audio;
using Xunit;

namespace NeuroMicrophone.Tests;

public class AutoGainControlTests
{
    [Fact]
    public void Read_QuietSpeechLevel_GainConvergesTowardTarget()
    {
        // Сигнал на уровне примерно -30 dBFS, цель — поднять его к -18 dBFS.
        float amplitude = LevelMeter.DbToLinear(-30f);
        float[] input = new float[48000 * 2]; // 2 секунды — с запасом для сходимости при Attack=50мс/Release=500мс
        for (int i = 0; i < input.Length; i++) input[i] = amplitude;

        var source = new ArraySampleProvider(input);
        var agc = new AutoGainControl(source) { TargetLevelDb = -18f, NoiseFloorDb = -55f };

        float[] output = new float[input.Length];
        agc.Read(output, 0, output.Length);

        // Проверяем "хвост" — огибающая должна успеть сойтись близко к цели.
        int tailStart = output.Length - 4800;
        float tailRms = LevelMeter.CalculateRmsDb(output, tailStart, 4800);

        Assert.InRange(tailRms, -19f, -17f);
    }

    [Fact]
    public void Read_BelowNoiseFloor_GainDoesNotChase()
    {
        // Сигнал тише порога NoiseFloorDb — это "просто фон", а не речь.
        float amplitude = LevelMeter.DbToLinear(-70f);
        float[] input = new float[48000];
        for (int i = 0; i < input.Length; i++) input[i] = amplitude;

        var source = new ArraySampleProvider(input);
        var agc = new AutoGainControl(source) { TargetLevelDb = -18f, NoiseFloorDb = -55f };

        float[] output = new float[input.Length];
        agc.Read(output, 0, output.Length);

        // Усиление должно остаться близким к начальному (0 дБ) — то есть
        // выходной уровень должен остаться около исходных -70 дБ, а НЕ
        // подняться к цели -18 дБ (что подняло бы фоновый шум).
        float tailRms = LevelMeter.CalculateRmsDb(output, output.Length - 4800, 4800);
        Assert.InRange(tailRms, -70.5f, -69.5f);
    }
}
