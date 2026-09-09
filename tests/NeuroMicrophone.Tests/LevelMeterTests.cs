using NeuroMicrophone.Audio;
using Xunit;

namespace NeuroMicrophone.Tests;

public class LevelMeterTests
{
    [Fact]
    public void CalculateRmsDb_FullScaleConstant_ReturnsZeroDb()
    {
        float[] buffer = { 1f, 1f, 1f, 1f };
        float rmsDb = LevelMeter.CalculateRmsDb(buffer, 0, buffer.Length);
        Assert.Equal(0.0, (double)rmsDb, precision: 3);
    }

    [Fact]
    public void CalculatePeakDb_FullScaleConstant_ReturnsZeroDb()
    {
        float[] buffer = { 1f, -1f, 0.5f, -0.5f };
        float peakDb = LevelMeter.CalculatePeakDb(buffer, 0, buffer.Length);
        Assert.Equal(0.0, (double)peakDb, precision: 3);
    }

    [Fact]
    public void CalculateRmsDb_Silence_ReturnsMinDb()
    {
        float[] buffer = { 0f, 0f, 0f, 0f };
        float rmsDb = LevelMeter.CalculateRmsDb(buffer, 0, buffer.Length);
        Assert.Equal(LevelMeter.MinDb, rmsDb);
    }

    [Fact]
    public void DbToLinear_And_LinearToDb_AreInverses()
    {
        const float original = -12f;
        float linear = LevelMeter.DbToLinear(original);
        float roundTrip = LevelMeter.LinearToDb(linear);
        Assert.Equal((double)original, (double)roundTrip, precision: 2);
    }

    [Fact]
    public void CalculateRmsDb_HalfAmplitude_ReturnsApproxMinus6Db()
    {
        float[] buffer = { 0.5f, -0.5f, 0.5f, -0.5f };
        float rmsDb = LevelMeter.CalculateRmsDb(buffer, 0, buffer.Length);
        Assert.Equal(-6.02, (double)rmsDb, precision: 1);
    }

    [Fact]
    public void CalculateRmsDb_UsesOffset_IgnoresDataOutsideRange()
    {
        // Первый сэмпл (индекс 0) намеренно "громкий" — он должен быть
        // проигнорирован, так как чтение начинается с offset=1.
        float[] buffer = { 1f, 0.5f, -0.5f, 0.5f, -0.5f };
        float rmsDb = LevelMeter.CalculateRmsDb(buffer, 1, 4);
        Assert.Equal(-6.02, (double)rmsDb, precision: 1);
    }
}
