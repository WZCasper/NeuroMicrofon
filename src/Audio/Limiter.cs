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
    private readonly float[] _delayedFrameScratch; // переиспользуемый буфер на один кадр (без аллокаций в Read)

    // Монотонная очередь для скользящего максимума пиков за окно
    // lookaheadFrames кадров — подробности алгоритма см. в комментарии
    // внутри Read(). Оба массива — кольцевые буферы ёмкостью lookaheadFrames:
    // это строгая верхняя граница числа элементов, которые когда-либо
    // одновременно лежат в очереди (при самой невыгодной — строго убывающей
    // — последовательности пиков очередь растёт не быстрее чем на 1 элемент
    // за кадр, а элементы старше lookaheadFrames кадров каждый раз
    // вытесняются с головы).
    private readonly int[] _dequeFramePos;
    private readonly float[] _dequeValue;
    private int _dequeHead;  // только аудиопоток
    private int _dequeCount; // только аудиопоток

    private int _writePos; // только аудиопоток — индекс кольцевого буфера; также монотонный номер кадра для очереди выше
    private volatile float _currentGain = 1f; // пишется на аудиопотоке, читается с потока UI через CurrentGainReductionDb

    // CeilingDb/ReleaseMs/Enabled пишутся с потока UI (биндинги через
    // DspViewModel) и читаются на аудиопотоке внутри каждого вызова Read()
    // — та же причина для volatile, что и в NoiseGate (см. комментарий там).
    private volatile float _ceilingDb = -2f;
    private volatile float _releaseMs = 100f;
    private volatile bool _enabled = true;
    private volatile bool _coefficientsDirty = true;

    // Кэш ceiling/release — пересчитывается только при изменении
    // соответствующего параметра, а не на каждый вызов Read() (тот же
    // приём, что уже применяется в HighPassFilter). Читается и пишется
    // только на аудиопотоке, поэтому volatile ему не нужен.
    private float _ceilingLinear;
    private float _releaseCoeff;

    public float CeilingDb
    {
        get => _ceilingDb;
        set { if (_ceilingDb == value) return; _ceilingDb = value; _coefficientsDirty = true; }
    }

    public float ReleaseMs
    {
        get => _releaseMs;
        set { if (_releaseMs == value) return; _releaseMs = value; _coefficientsDirty = true; }
    }

    public bool Enabled { get => _enabled; set => _enabled = value; }

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
        _delayedFrameScratch = new float[_channels];
        _dequeFramePos = new int[_lookaheadFrames];
        _dequeValue = new float[_lookaheadFrames];
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int samplesRead = _source.Read(buffer, offset, count);
        if (samplesRead <= 0 || !Enabled) return samplesRead;

        RecomputeCoefficientsIfNeeded();

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

            // Сохраняем текущий кадр в линию задержки и одновременно забираем
            // тот, что был записан lookaheadFrames кадров назад — именно его
            // мы сейчас выводим (задержанный, но уже "просчитанный" сигнал).
            int delayIndex = ringIndex * _channels;
            for (int ch = 0; ch < _channels; ch++)
            {
                _delayedFrameScratch[ch] = _delayBuffer[delayIndex + ch];
                _delayBuffer[delayIndex + ch] = buffer[frameOffset + ch];
            }

            // Скользящий максимум пиков за окно lookaheadFrames кадров — O(1)
            // амортизированно через монотонную очередь, вместо полного
            // перебора всего окна на каждый кадр (раньше — O(lookaheadFrames)
            // сравнений на КАЖДЫЙ сэмпл; при 5 мс/48 кГц lookaheadFrames=240).
            // Очередь хранит пары (номер кадра, пик) по невозрастанию пика:
            // любой уже сохранённый кадр, чей пик не больше пика нового кадра,
            // впредь не может стать максимумом окна раньше нового — такие
            // кадры сразу выбрасываются с хвоста. Текущий максимум окна всегда
            // лежит в голове очереди, а кадры старше lookaheadFrames шагов
            // вытесняются оттуда по мере сдвига окна — то же самое
            // "забывание", которое раньше происходило само по себе за счёт
            // перезаписи кольцевого буфера пиков.
            while (_dequeCount > 0 && _dequeValue[(_dequeHead + _dequeCount - 1) % _lookaheadFrames] <= incomingPeak)
            {
                _dequeCount--;
            }
            int dequeTailIndex = (_dequeHead + _dequeCount) % _lookaheadFrames;
            _dequeFramePos[dequeTailIndex] = _writePos;
            _dequeValue[dequeTailIndex] = incomingPeak;
            _dequeCount++;

            while (_writePos - _dequeFramePos[_dequeHead] >= _lookaheadFrames)
            {
                _dequeHead = (_dequeHead + 1) % _lookaheadFrames;
                _dequeCount--;
            }

            float windowPeak = _dequeValue[_dequeHead];

            float requiredGain = windowPeak > _ceilingLinear ? _ceilingLinear / windowPeak : 1f;

            // Усиление может упасть мгновенно (гарантия непревышения потолка),
            // но восстанавливается плавно по времени Release.
            if (requiredGain < _currentGain)
            {
                _currentGain = requiredGain;
            }
            else
            {
                _currentGain += (requiredGain - _currentGain) * _releaseCoeff;
            }

            for (int ch = 0; ch < _channels; ch++)
            {
                float sample = _delayedFrameScratch[ch] * _currentGain;
                // Финальный предохранитель: гарантирует потолок даже при экстремальных транзиентах.
                sample = Math.Clamp(sample, -_ceilingLinear, _ceilingLinear);
                buffer[frameOffset + ch] = sample;
            }

            _writePos++;
        }

        return samplesRead;
    }

    private void RecomputeCoefficientsIfNeeded()
    {
        if (!_coefficientsDirty) return;

        _ceilingLinear = LevelMeter.DbToLinear(_ceilingDb);
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
