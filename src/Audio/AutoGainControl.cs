using System;
using NAudio.Wave;

namespace NeuroMicrophone.Audio;

/// <summary>
/// Автоматическая регулировка усиления: непрерывно подстраивает
/// коэффициент усиления так, чтобы сглаженный RMS-уровень речи
/// стремился к TargetLevelDb (по умолчанию -18 dBFS, по спецификации
/// калибровки).
///
/// Важно: пока входной уровень ниже NoiseFloorDb (то есть говорящий
/// молчит и слышен только фон), усиление НЕ поднимается — иначе AGC
/// начал бы "раскачивать" тишину до целевого уровня и поднимать шум
/// на паузах между фразами.
/// </summary>
public sealed class AutoGainControl : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly int _sampleRate;

    // Коэффициент RMS-детектора (окно ~300 мс) зависит только от частоты
    // дискретизации, которая не меняется после создания — вычисляется
    // один раз в конструкторе, а не на каждый вызов Read().
    private readonly double _rmsCoeff;

    private double _runningMeanSquare;
    private float _currentGainDb;

    private float _attackMs = 50f;
    private float _releaseMs = 500f;
    private bool _coefficientsDirty = true;

    // Кэш attack/release — пересчитывается только при изменении
    // соответствующего параметра, а не на каждый вызов Read() (тот же
    // приём, что уже применяется в HighPassFilter).
    private float _attackCoeff;
    private float _releaseCoeff;

    public float TargetLevelDb { get; set; } = -18f;
    public float NoiseFloorDb { get; set; } = -55f;
    public float MaxGainDb { get; set; } = 24f;
    public float MinGainDb { get; set; } = -12f;

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

    public bool Enabled { get; set; } = true;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public AutoGainControl(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        _sampleRate = source.WaveFormat.SampleRate;
        _rmsCoeff = 1.0 - Math.Exp(-1.0 / (0.3 * _sampleRate));
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

            double rms = Math.Sqrt(_runningMeanSquare);
            float currentDb = LevelMeter.LinearToDb(rms);

            if (currentDb >= NoiseFloorDb)
            {
                float desiredGainDb = Math.Clamp(TargetLevelDb - currentDb, MinGainDb, MaxGainDb);
                float coeff = desiredGainDb < _currentGainDb ? _attackCoeff : _releaseCoeff;
                _currentGainDb += (desiredGainDb - _currentGainDb) * coeff;
            }
            // иначе: сигнал — это фоновый шум, а не речь; усиление удерживается на текущем значении.

            float gainLinear = LevelMeter.DbToLinear(_currentGainDb);
            for (int ch = 0; ch < _channels; ch++)
            {
                buffer[frameOffset + ch] *= gainLinear;
            }
        }

        return samplesRead;
    }

    private void RecomputeCoefficientsIfNeeded()
    {
        if (!_coefficientsDirty) return;

        _attackCoeff = ComputeCoefficient(_attackMs);
        _releaseCoeff = ComputeCoefficient(_releaseMs);
        _coefficientsDirty = false;
    }

    private float ComputeCoefficient(float timeMs)
    {
        if (timeMs <= 0f) return 1f;
        float timeConstantSeconds = timeMs / 1000f;
        return 1f - (float)Math.Exp(-1.0 / (timeConstantSeconds * _sampleRate));
    }
}
