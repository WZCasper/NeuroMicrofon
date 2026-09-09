using System;
using NAudio.Wave;

namespace NeuroMicrophone.Audio;

/// <summary>
/// Брикволл-лимитер с небольшим лукахедом (по умолчанию 5 мс): пик
/// "видно" заранее через кольцевой буфер, поэтому усиление успевает
/// плавно снизиться ДО того, как громкий сэмпл дойдёт до выхода —
/// это даёт более чистое ограничение, чем реакция постфактум.
///
/// Дополнительно на выходе стоит жёсткий Math.Clamp по потолку —
/// он гарантирует, что даже экстремально быстрый транзиент, который
/// огибающая не успела отследить, физически не превысит CeilingDb.
/// Это соответствует требованию "permanently prevent audio clipping".
/// </summary>
public sealed class Limiter : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly int _lookaheadFrames;
    private readonly float[] _delayBuffer;      // кольцевой буфер задержанных сэмплов, lookaheadFrames * channels
    private readonly float[] _peakWindow;        // кольцевой буфер пиков по кадрам, lookaheadFrames
    private readonly float[] _delayedFrameScratch; // переиспользуемый буфер на один кадр (без аллокаций в Read)
    private int _writePos;
    private float _currentGain = 1f;

    public float CeilingDb { get; set; } = -2f;
    public float ReleaseMs { get; set; } = 100f;
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Текущее (сглаженное) снижение усиления в дБ, значение &lt;= 0.
    /// 0 означает, что лимитер сейчас не вмешивается в сигнал.
    /// </summary>
    public float CurrentGainReductionDb => LevelMeter.LinearToDb(_currentGain);

    public WaveFormat WaveFormat => _source.WaveFormat;

    public Limiter(ISampleProvider source, float lookaheadMs = 5f)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        _sampleRate = source.WaveFormat.SampleRate;
        _lookaheadFrames = Math.Max(1, (int)(lookaheadMs / 1000f * _sampleRate));
        _delayBuffer = new float[_lookaheadFrames * _channels];
        _peakWindow = new float[_lookaheadFrames];
        _delayedFrameScratch = new float[_channels];
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int samplesRead = _source.Read(buffer, offset, count);
        if (samplesRead <= 0 || !Enabled) return samplesRead;

        float ceilingLinear = LevelMeter.DbToLinear(CeilingDb);
        float releaseCoeff = ComputeCoefficient(ReleaseMs);
        int frames = samplesRead / _channels;

        for (int frame = 0; frame < frames; frame++)
        {
            int frameOffset = offset + frame * _channels;

            float incomingPeak = 0f;
            for (int ch = 0; ch < _channels; ch++)
            {
                float abs = Math.Abs(buffer[frameOffset + ch]);
                if (abs > incomingPeak) incomingPeak = abs;
            }

            int ringIndex = _writePos % _lookaheadFrames;
            _peakWindow[ringIndex] = incomingPeak;

            // Сохраняем текущий кадр в линию задержки и одновременно забираем
            // тот, что был записан lookaheadFrames кадров назад — именно его
            // мы сейчас выводим (задержанный, но уже "просчитанный" сигнал).
            int delayIndex = ringIndex * _channels;
            for (int ch = 0; ch < _channels; ch++)
            {
                _delayedFrameScratch[ch] = _delayBuffer[delayIndex + ch];
                _delayBuffer[delayIndex + ch] = buffer[frameOffset + ch];
            }

            float windowPeak = 0f;
            for (int i = 0; i < _lookaheadFrames; i++)
            {
                if (_peakWindow[i] > windowPeak) windowPeak = _peakWindow[i];
            }

            float requiredGain = windowPeak > ceilingLinear ? ceilingLinear / windowPeak : 1f;

            // Усиление может упасть мгновенно (гарантия непревышения потолка),
            // но восстанавливается плавно по времени Release.
            if (requiredGain < _currentGain)
            {
                _currentGain = requiredGain;
            }
            else
            {
                _currentGain += (requiredGain - _currentGain) * releaseCoeff;
            }

            for (int ch = 0; ch < _channels; ch++)
            {
                float sample = _delayedFrameScratch[ch] * _currentGain;
                // Финальный предохранитель: гарантирует потолок даже при экстремальных транзиентах.
                sample = Math.Clamp(sample, -ceilingLinear, ceilingLinear);
                buffer[frameOffset + ch] = sample;
            }

            _writePos++;
        }

        return samplesRead;
    }

    private float ComputeCoefficient(float timeMs)
    {
        if (timeMs <= 0f) return 1f;
        float timeConstantSeconds = timeMs / 1000f;
        return 1f - (float)Math.Exp(-1.0 / (timeConstantSeconds * _sampleRate));
    }
}
