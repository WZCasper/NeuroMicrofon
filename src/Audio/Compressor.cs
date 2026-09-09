using System;
using NAudio.Wave;

namespace NeuroMicrophone.Audio;

/// <summary>
/// Feed-forward компрессор с RMS-детектором и мягким коленом (soft knee).
/// Формула основана на классическом описании цифрового компрессора
/// (Giannoulis, Massberg, Reiss — "Digital Dynamic Range Compressor Design"):
///
///   y = x,                                              x &lt; T - W/2
///   y = x + (1/R - 1) * (x - T + W/2)^2 / (2W),          T - W/2 &lt;= x &lt;= T + W/2
///   y = T + (x - T) / R,                                 x &gt; T + W/2
///
/// где x/y — входной/выходной уровень в дБ, T — порог (ThresholdDb),
/// R — коэффициент сжатия (Ratio), W — ширина колена (KneeDb).
/// Итоговое снижение усиления (в дБ) сглаживается по времени Attack/Release.
/// </summary>
public sealed class Compressor : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly int _sampleRate;
    private double _runningMeanSquare;
    private float _currentGainReductionDb;

    public float ThresholdDb { get; set; } = -12f;
    public float Ratio { get; set; } = 3f;
    public float KneeDb { get; set; } = 6f;
    public float AttackMs { get; set; } = 10f;
    public float ReleaseMs { get; set; } = 150f;
    public float MakeupGainDb { get; set; } = 0f;
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Текущее (сглаженное) снижение усиления в дБ, значение &lt;= 0.
    /// Используется индикатором Gain Reduction в UI, чтобы показать
    /// пользователю, насколько активно компрессор сейчас работает.
    /// </summary>
    public float CurrentGainReductionDb => _currentGainReductionDb;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public Compressor(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        _sampleRate = source.WaveFormat.SampleRate;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int samplesRead = _source.Read(buffer, offset, count);
        if (!Enabled || samplesRead <= 0) return samplesRead;

        int frames = samplesRead / _channels;

        // Детектор с окном ~20 мс — классический выбор для голосовых компрессоров.
        double rmsCoeff = 1.0 - Math.Exp(-1.0 / (0.02 * _sampleRate));
        float attackCoeff = ComputeCoefficient(AttackMs);
        float releaseCoeff = ComputeCoefficient(ReleaseMs);
        float makeupLinear = LevelMeter.DbToLinear(MakeupGainDb);

        for (int frame = 0; frame < frames; frame++)
        {
            int frameOffset = offset + frame * _channels;

            double frameSumSquares = 0;
            for (int ch = 0; ch < _channels; ch++)
            {
                float s = buffer[frameOffset + ch];
                frameSumSquares += s * s;
            }
            double frameMeanSquare = frameSumSquares / _channels;
            _runningMeanSquare += (frameMeanSquare - _runningMeanSquare) * rmsCoeff;

            double rms = Math.Sqrt(Math.Max(_runningMeanSquare, 1e-12));
            float inputDb = LevelMeter.LinearToDb(rms);

            float targetGainReductionDb = ComputeGainReduction(inputDb);

            float coeff = targetGainReductionDb < _currentGainReductionDb ? attackCoeff : releaseCoeff;
            _currentGainReductionDb += (targetGainReductionDb - _currentGainReductionDb) * coeff;

            float totalGain = LevelMeter.DbToLinear(_currentGainReductionDb) * makeupLinear;
            for (int ch = 0; ch < _channels; ch++)
            {
                buffer[frameOffset + ch] *= totalGain;
            }
        }

        return samplesRead;
    }

    /// <summary>
    /// Возвращает требуемое снижение усиления (значение &lt;= 0 дБ) для
    /// заданного уровня детектора, с учётом мягкого колена вокруг порога.
    /// </summary>
    private float ComputeGainReduction(float inputDb)
    {
        float overshoot = inputDb - ThresholdDb;

        if (KneeDb <= 0f)
        {
            // Жёсткое колено: ниже порога — без изменений, выше — линейное сжатие.
            return overshoot <= 0f ? 0f : (ThresholdDb + overshoot / Ratio) - inputDb;
        }

        float halfKnee = KneeDb / 2f;

        if (overshoot < -halfKnee)
        {
            return 0f;
        }

        if (overshoot > halfKnee)
        {
            float outputDb = ThresholdDb + overshoot / Ratio;
            return outputDb - inputDb;
        }

        // Внутри зоны колена — квадратичная интерполяция между "без сжатия" и "полное сжатие".
        float kneeTerm = overshoot + halfKnee;
        return (1f / Ratio - 1f) * (kneeTerm * kneeTerm) / (2f * KneeDb);
    }

    private float ComputeCoefficient(float timeMs)
    {
        if (timeMs <= 0f) return 1f;
        float timeConstantSeconds = timeMs / 1000f;
        return 1f - (float)Math.Exp(-1.0 / (timeConstantSeconds * _sampleRate));
    }
}
