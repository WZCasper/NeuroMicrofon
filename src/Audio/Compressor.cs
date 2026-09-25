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

    // Коэффициент RMS-детектора (окно ~20 мс) зависит только от частоты
    // дискретизации, которая не меняется после создания — вычисляется
    // один раз в конструкторе, а не на каждый вызов Read().
    private readonly double _rmsCoeff;

    private double _runningMeanSquare;
    private float _currentGainReductionDb;

    private float _attackMs = 10f;
    private float _releaseMs = 150f;
    private float _makeupGainDb;
    private bool _coefficientsDirty = true;

    // Кэш attack/release/makeup — пересчитывается только при изменении
    // соответствующего параметра, а не на каждый вызов Read() (тот же
    // приём, что уже применяется в HighPassFilter).
    private float _attackCoeff;
    private float _releaseCoeff;
    private float _makeupLinear = 1f;

    public float ThresholdDb { get; set; } = -12f;
    public float Ratio { get; set; } = 3f;
    public float KneeDb { get; set; } = 6f;

    public float AttackMs
    {
        get => _attackMs;
        set { if (_attackMs == value) return; _attackMs = value; _coefficientsDirty = true; }
    }

    public float ReleaseMs
    {
        get => _releaseMs;
        set { if (_releaseMs == value) return; _releaseMs = value; _coefficientsDirty = true; }
    }

    public float MakeupGainDb
    {
        get => _makeupGainDb;
        set { if (_makeupGainDb == value) return; _makeupGainDb = value; _coefficientsDirty = true; }
    }

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
        _rmsCoeff = 1.0 - Math.Exp(-1.0 / (0.02 * _sampleRate));
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int samplesRead = _source.Read(buffer, offset, count);
        if (!Enabled || samplesRead <= 0) return samplesRead;

        RecomputeCoefficientsIfNeeded();

        int frames = samplesRead / _channels;

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
            _runningMeanSquare += (frameMeanSquare - _runningMeanSquare) * _rmsCoeff;

            double rms = Math.Sqrt(Math.Max(_runningMeanSquare, 1e-12));
            float inputDb = LevelMeter.LinearToDb(rms);

            float targetGainReductionDb = ComputeGainReduction(inputDb);

            float coeff = targetGainReductionDb < _currentGainReductionDb ? _attackCoeff : _releaseCoeff;
            _currentGainReductionDb += (targetGainReductionDb - _currentGainReductionDb) * coeff;

            float totalGain = LevelMeter.DbToLinear(_currentGainReductionDb) * _makeupLinear;
            for (int ch = 0; ch < _channels; ch++)
            {
                buffer[frameOffset + ch] *= totalGain;
            }
        }

        return samplesRead;
    }

    private void RecomputeCoefficientsIfNeeded()
    {
        if (!_coefficientsDirty) return;

        _attackCoeff = ComputeCoefficient(_attackMs);
        _releaseCoeff = ComputeCoefficient(_releaseMs);
        _makeupLinear = LevelMeter.DbToLinear(_makeupGainDb);
        _coefficientsDirty = false;
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
