using System;
using NAudio.Wave;

namespace NeuroMicrophone.Audio;

/// <summary>
/// Шумовой гейт: полностью приглушает сигнал ниже порога ThresholdDb
/// и плавно открывается/закрывается по времени Attack/Release, чтобы
/// избежать резких щелчков на границах речи.
/// Порог задаётся движком калибровки как "уровень фонового шума + 6 дБ".
///
/// Между "сигнал пропал" и началом фазы Release есть ещё фаза Hold:
/// гейт держится полностью открытым ещё HoldMs миллисекунд после того,
/// как сигнал упал ниже порога, и только потом начинает закрываться.
/// Без этой фазы гейт на быстро колеблющемся уровне речи (например,
/// на шипящих согласных) может "стрекотать" — быстро открываться и
/// закрываться десятки раз в секунду.
///
/// Если передан источник VAD (обычно — сам RnnoiseDenoiser), гейт
/// открывается по правилу "ИЛИ": громкость выше порога ИЛИ вероятность
/// голоса выше VadThreshold. Это устраняет типичную проблему гейта на
/// чистом пороге по амплитуде — обрезание тихих окончаний слов, когда
/// громкость уже упала, а речь ещё продолжается. VAD-значение относится
/// к последнему обработанному 10-мс кадру RNNoise и обновляется реже,
/// чем вызывается Read(), поэтому вносит небольшую, но приемлемую
/// задержку — она сглаживается тем же механизмом Attack/Release.
/// </summary>
public sealed class NoiseGate : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly IVoiceActivitySource? _vadSource;
    private readonly int _sampleRate;
    private readonly int _channels;
    private float _envelope = 1f; // текущий применяемый коэффициент усиления, 0..1
    private int _holdRemainingFrames;

    public float ThresholdDb { get; set; } = -50f;
    public float AttackMs { get; set; } = 5f;
    public float HoldMs { get; set; } = 100f;
    public float ReleaseMs { get; set; } = 150f;
    public float VadThreshold { get; set; } = 0.6f;
    public bool Enabled { get; set; } = true;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public NoiseGate(ISampleProvider source, IVoiceActivitySource? vadSource = null)
    {
        _source = source;
        _vadSource = vadSource;
        _sampleRate = source.WaveFormat.SampleRate;
        _channels = source.WaveFormat.Channels;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int samplesRead = _source.Read(buffer, offset, count);
        if (!Enabled || samplesRead <= 0) return samplesRead;

        float thresholdLinear = LevelMeter.DbToLinear(ThresholdDb);
        float attackCoeff = ComputeCoefficient(AttackMs);
        float releaseCoeff = ComputeCoefficient(ReleaseMs);
        int holdFrames = Math.Max(0, (int)(HoldMs / 1000f * _sampleRate));

        // VAD-значение читается один раз на весь вызов Read (а не на каждый
        // сэмпл) — оно и так обновляется реже, чем сэмплы поступают, так что
        // дополнительная точность здесь не нужна, а лишние чтения volatile-поля
        // в плотном цикле по сэмплам были бы напрасны.
        bool vadOpen = _vadSource != null && _vadSource.IsAvailable && _vadSource.VadProbability >= VadThreshold;

        int frames = samplesRead / _channels;
        for (int frame = 0; frame < frames; frame++)
        {
            int frameOffset = offset + frame * _channels;

            // Детектор берёт максимум по модулю среди каналов кадра,
            // чтобы гейт открывался/закрывался синхронно для всех каналов.
            float frameMax = 0f;
            for (int ch = 0; ch < _channels; ch++)
            {
                float abs = Math.Abs(buffer[frameOffset + ch]);
                if (abs > frameMax) frameMax = abs;
            }

            bool signalPresent = frameMax >= thresholdLinear || vadOpen;
            float targetGain;

            if (signalPresent)
            {
                _holdRemainingFrames = holdFrames;
                targetGain = 1f;
            }
            else if (_holdRemainingFrames > 0)
            {
                _holdRemainingFrames--;
                targetGain = 1f; // ещё в фазе Hold — держим открытым
            }
            else
            {
                targetGain = 0f; // Hold закончился — начинаем закрываться по Release
            }

            float coeff = targetGain > _envelope ? attackCoeff : releaseCoeff;
            _envelope += (targetGain - _envelope) * coeff;

            for (int ch = 0; ch < _channels; ch++)
            {
                buffer[frameOffset + ch] *= _envelope;
            }
        }

        return samplesRead;
    }

    /// <summary>
    /// Переводит время в миллисекундах в коэффициент экспоненциального
    /// сглаживания для текущей частоты дискретизации (стандартная формула
    /// одно-полюсного envelope follower'а).
    /// </summary>
    private float ComputeCoefficient(float timeMs)
    {
        if (timeMs <= 0f) return 1f;
        float timeConstantSeconds = timeMs / 1000f;
        return 1f - (float)Math.Exp(-1.0 / (timeConstantSeconds * _sampleRate));
    }
}
