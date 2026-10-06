using System;
using System.Globalization;
using System.Windows.Data;
using NeuroMicrophone.Audio;

namespace NeuroMicrophone.Converters;

/// <summary>
/// Переводит значение уровня в дБFS (диапазон LevelMeter.MinDb..0) в высоту
/// столбика визуализатора в пикселях. ConverterParameter — высота контейнера
/// в пикселях (строка, например "96"), чтобы один и тот же конвертер можно
/// было использовать для контейнеров разной высоты без отдельного класса на
/// каждый размер.
/// </summary>
public sealed class DbLevelToBarHeightConverter : IValueConverter
{
    // Минимальная доля высоты, видимая даже на полной тишине — чтобы
    // столбик читался как "ноль сейчас", а не пропадал совсем (что выглядело
    // бы как сбой, а не как тишина).
    private const double MinVisibleFraction = 0.02;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double db = value is double d ? d : LevelMeter.MinDb;
        double containerHeight = parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double h)
            ? h
            : 96.0;

        double range = 0.0 - LevelMeter.MinDb;
        double fraction = range > 0 ? (db - LevelMeter.MinDb) / range : 0.0;
        fraction = Math.Clamp(fraction, MinVisibleFraction, 1.0);

        return fraction * containerHeight;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException("Обратное преобразование высоты столбика не требуется.");
    }
}
