using System;
using System.Globalization;
using System.Windows.Data;

namespace NeuroMicrophone.Converters;

/// <summary>
/// Переводит значение снижения усиления в дБ (0 = нет сжатия, отрицательное —
/// сжатие) в проценты заполнения индикатора (0..100), где 0 дБ → 0%,
/// а -24 дБ и ниже → 100%. Используется для индикаторов Gain Reduction
/// компрессора и лимитера.
/// </summary>
public sealed class GainReductionToPercentConverter : IValueConverter
{
    private const double RangeDb = 24.0;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double db = value is double d ? d : 0.0;
        double clamped = Math.Clamp(-db, 0.0, RangeDb);
        return clamped / RangeDb * 100.0;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
