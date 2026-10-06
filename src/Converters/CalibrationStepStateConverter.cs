using System;
using System.Globalization;
using System.Windows.Data;

namespace NeuroMicrophone.Converters;

/// <summary>
/// Состояние одной строки списка из 4 шагов автонастройки для заданного
/// номера шага (передаётся в ConverterParameter, т.к. он фиксирован для
/// конкретной строки разметки и в привязке не нуждается).
/// Входы: [0] CalibrationViewModel.CurrentStepNumber (int, 0 — калибровка
/// ещё не запускалась), [1] CalibrationViewModel.IsCalibrating (bool).
/// Результат — одна из трёх строк: "pending" (ещё не дошли), "active"
/// (идёт прямо сейчас) или "done" (этап пройден, в том числе все 4 после
/// успешного завершения всей калибровки).
/// </summary>
public sealed class CalibrationStepStateConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        int currentStep = values.Length > 0 && values[0] is int i ? i : 0;
        bool isCalibrating = values.Length > 1 && values[1] is bool b && b;
        int thisStep = parameter is string s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;

        if (currentStep > thisStep || (currentStep == thisStep && !isCalibrating && currentStep > 0))
        {
            return "done";
        }

        if (currentStep == thisStep && isCalibrating)
        {
            return "active";
        }

        return "pending";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException("Обратное преобразование состояния шага калибровки не требуется.");
    }
}
