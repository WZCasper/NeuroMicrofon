using System;
using System.Globalization;
using System.Windows.Data;

namespace NeuroMicrophone.Converters;

/// <summary>
/// Сравнивает имя пресета (первое значение) с названием активного набора
/// настроек ViewModel (второе значение) — используется в MultiDataTrigger,
/// чтобы подсветить карточку того пресета, который сейчас реально применён.
/// </summary>
public sealed class PresetActiveConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2) return false;
        string? presetName = values[0] as string;
        string? activeLabel = values[1] as string;
        return !string.IsNullOrEmpty(presetName) && string.Equals(presetName, activeLabel, StringComparison.Ordinal);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
