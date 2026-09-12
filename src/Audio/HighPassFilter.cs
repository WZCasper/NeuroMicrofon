using System;
using NAudio.Wave;

namespace NeuroMicrophone.Audio;

/// <summary>
/// Биквадратный (biquad) фильтр верхних частот по стандартным формулам
/// из "Audio EQ Cookbook" (Robert Bristow-Johnson) — общепринятого
/// эталонного описания цифровых биквадратных фильтров.
///
/// Стоит первым в DSP-цепочке (до шумоподавления): срезает гул
/// кондиционеров/вентиляторов/сетевой наводки (обычно ниже 60-80 Гц) и
/// смягчает "плевки" на взрывных согласных (П, Б), которые иначе создают
/// низкочастотные всплески, лишний раз нагружающие AGC и компрессор.
///
/// Q = 0.707 (по умолчанию) соответствует фильтру Баттерворта —
/// максимально плоская амплитудно-частотная характеристика в полосе
/// пропускания, без резонансного "горба" около частоты среза.
/// </summary>
public sealed class HighPassFilter : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _sampleRate;

    private float _cutoffHz = 90f;
    private float _q = 0.707f;
    private bool _coefficientsDirty = true;

    private float _b0, _b1, _b2, _a1, _a2;
    private float _x1, _x2, _y1, _y2;

    public bool Enabled { get; set; } = true;

    public float CutoffHz
    {
        get => _cutoffHz;
        set
        {
            if (Math.Abs(_cutoffHz - value) < 0.01f) return;
            _cutoffHz = value;
            _coefficientsDirty = true;
        }
    }

    public float Q
    {
        get => _q;
        set
        {
            if (Math.Abs(_q - value) < 0.001f) return;
            _q = value;
            _coefficientsDirty = true;
        }
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public HighPassFilter(ISampleProvider source)
    {
        if (source.WaveFormat.Channels != 1)
        {
            throw new ArgumentException("HighPassFilter рассчитан на монофонический источник.", nameof(source));
        }

        _source = source;
        _sampleRate = source.WaveFormat.SampleRate;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int samplesRead = _source.Read(buffer, offset, count);
        if (!Enabled || samplesRead <= 0) return samplesRead;

        if (_coefficientsDirty)
        {
            RecomputeCoefficients();
        }

        for (int i = 0; i < samplesRead; i++)
        {
            float x0 = buffer[offset + i];
            float y0 = _b0 * x0 + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;

            _x2 = _x1;
            _x1 = x0;
            _y2 = _y1;
            _y1 = y0;

            buffer[offset + i] = y0;
        }

        return samplesRead;
    }

    /// <summary>
    /// Пересчитывает коэффициенты биквадратного HPF по формулам Audio EQ
    /// Cookbook. Вызывается только при изменении CutoffHz/Q, а не на
    /// каждый сэмпл — коэффициенты кэшируются между вызовами Read.
    /// </summary>
    private void RecomputeCoefficients()
    {
        double w0 = 2.0 * Math.PI * _cutoffHz / _sampleRate;
        double cosw0 = Math.Cos(w0);
        double alpha = Math.Sin(w0) / (2.0 * _q);

        double b0 = (1.0 + cosw0) / 2.0;
        double b1 = -(1.0 + cosw0);
        double b2 = (1.0 + cosw0) / 2.0;
        double a0 = 1.0 + alpha;
        double a1 = -2.0 * cosw0;
        double a2 = 1.0 - alpha;

        _b0 = (float)(b0 / a0);
        _b1 = (float)(b1 / a0);
        _b2 = (float)(b2 / a0);
        _a1 = (float)(a1 / a0);
        _a2 = (float)(a2 / a0);

        _coefficientsDirty = false;
    }
}
