using System;

namespace NeuroMicrophone.Audio;

/// <summary>
/// Статические функции пересчёта между линейной амплитудой float-сэмплов
/// (диапазон, ожидаемый ISampleProvider: приблизительно [-1.0, 1.0]) и
/// децибелами dBFS. Используется всеми стадиями DSP-цепочки и метрами
/// уровня, поэтому вынесено в отдельный класс без побочных эффектов.
/// </summary>
public static class LevelMeter
{
    /// <summary>
    /// Нижняя граница шкалы. Ниже неё сигнал считается практически
    /// нулевым — это защищает Math.Log10 от -Infinity на тишине.
    /// </summary>
    public const float MinDb = -90f;

    /// <summary>
    /// RMS (среднеквадратичный уровень) участка буфера в dBFS.
    /// </summary>
    public static float CalculateRmsDb(float[] buffer, int offset, int count)
    {
        if (count <= 0) return MinDb;

        double sumSquares = 0;
        for (int i = 0; i < count; i++)
        {
            double sample = buffer[offset + i];
            sumSquares += sample * sample;
        }

        double rms = Math.Sqrt(sumSquares / count);
        return LinearToDb(rms);
    }

    /// <summary>
    /// Пиковый (по модулю) уровень участка буфера в dBFS.
    /// </summary>
    public static float CalculatePeakDb(float[] buffer, int offset, int count)
    {
        if (count <= 0) return MinDb;

        float peak = 0f;
        for (int i = 0; i < count; i++)
        {
            float abs = Math.Abs(buffer[offset + i]);
            if (abs > peak) peak = abs;
        }

        return LinearToDb(peak);
    }

    /// <summary>
    /// Переводит линейную амплитуду в dBFS, ограничивая результат снизу MinDb.
    /// </summary>
    public static float LinearToDb(double linear)
    {
        // Порог соответствует примерно -150 dB — ниже этого разумно считать "тишина",
        // чтобы не вычислять Log10 от почти нулевых чисел, что даёт неинформативный шум.
        const double silenceFloor = 0.0000000298;
        if (linear <= silenceFloor) return MinDb;

        double db = 20.0 * Math.Log10(linear);
        return (float)Math.Max(db, MinDb);
    }

    /// <summary>
    /// Переводит dBFS обратно в линейный коэффициент амплитуды.
    /// </summary>
    public static float DbToLinear(double db)
    {
        return (float)Math.Pow(10.0, db / 20.0);
    }
}
