using System.Globalization;
using System.Windows;
using NeuroMicrophone.Converters;
using Xunit;

namespace NeuroMicrophone.Tests;

public class ConvertersTests
{
    [Theory]
    [InlineData(true, Visibility.Collapsed)]
    [InlineData(false, Visibility.Visible)]
    public void InverseBoolToVisibilityConverter_MapsCorrectly(bool input, Visibility expected)
    {
        var converter = new InverseBoolToVisibilityConverter();
        Assert.Equal(expected, converter.Convert(input, typeof(Visibility), null, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(null, Visibility.Collapsed)]
    [InlineData("", Visibility.Collapsed)]
    [InlineData("   ", Visibility.Collapsed)]
    [InlineData("Драйвер не найден", Visibility.Visible)]
    public void NullToVisibilityConverter_MapsCorrectly(string? input, Visibility expected)
    {
        var converter = new NullToVisibilityConverter();
        Assert.Equal(expected, converter.Convert(input, typeof(Visibility), null, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(1, 2, false, "done")]      // шаг 1 давно пройден — сейчас идёт шаг 2
    [InlineData(2, 2, true, "active")]     // шаг 2 идёт прямо сейчас
    [InlineData(2, 2, false, "done")]      // шаг 2 завершён, калибровка уже не идёт
    [InlineData(2, 1, false, "pending")]   // шаг 2 ещё впереди — сейчас только 1-й
    [InlineData(2, 0, true, "pending")]    // калибровка ещё не запускалась (CurrentStepNumber=0)
    public void CalibrationStepStateConverter_ReturnsExpectedState(
        int thisStep, int currentStep, bool isCalibrating, string expected)
    {
        var converter = new CalibrationStepStateConverter();
        object result = converter.Convert(
            new object[] { currentStep, isCalibrating }, typeof(string), thisStep.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(-90.0, "96", 0.02 * 96)]  // тишина — минимально видимая высота, не ноль
    [InlineData(0.0, "96", 96.0)]          // максимальный уровень — вся высота контейнера
    [InlineData(-45.0, "96", 48.0)]        // середина диапазона -90..0 — примерно половина высоты
    public void DbLevelToBarHeightConverter_MapsRangeToPixelHeight(double db, string containerHeight, double expected)
    {
        var converter = new DbLevelToBarHeightConverter();
        object result = converter.Convert(db, typeof(double), containerHeight, CultureInfo.InvariantCulture);

        Assert.Equal(expected, (double)result, precision: 3);
    }

    [Theory]
    [InlineData(0.0, 0.0)]     // нет снижения усиления — индикатор пуст
    [InlineData(-24.0, 100.0)] // -24 дБ и ниже — индикатор полностью заполнен
    [InlineData(-12.0, 50.0)]  // середина шкалы 0..-24 дБ
    [InlineData(-40.0, 100.0)] // за пределами шкалы — не превышает 100%
    public void GainReductionToPercentConverter_MapsDbToPercent(double db, double expectedPercent)
    {
        var converter = new GainReductionToPercentConverter();
        object result = converter.Convert(db, typeof(double), null, CultureInfo.InvariantCulture);

        Assert.Equal(expectedPercent, (double)result, precision: 3);
    }

    [Fact]
    public void PresetActiveConverter_MatchingNames_ReturnsTrue()
    {
        var converter = new PresetActiveConverter();
        object result = converter.Convert(
            new object[] { "Подкаст", "Подкаст" }, typeof(bool), null, CultureInfo.InvariantCulture);

        Assert.Equal(true, result);
    }

    [Theory]
    [InlineData("Подкаст", "Стрим")]
    [InlineData(null, "Стрим")]
    public void PresetActiveConverter_NonMatchingOrMissingNames_ReturnsFalse(string? presetName, string activeLabel)
    {
        var converter = new PresetActiveConverter();
        object result = converter.Convert(
            new object?[] { presetName, activeLabel }, typeof(bool), null, CultureInfo.InvariantCulture);

        Assert.Equal(false, result);
    }
}
