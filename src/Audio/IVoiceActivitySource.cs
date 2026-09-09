namespace NeuroMicrophone.Audio;

/// <summary>
/// Источник вероятности голосовой активности (Voice Activity Detection) для
/// текущего обрабатываемого кадра. Реализуется RnnoiseDenoiser на основе
/// фактического возвращаемого значения rnnoise_process_frame.
/// </summary>
public interface IVoiceActivitySource
{
    /// <summary>Вероятность того, что текущий кадр содержит речь, от 0.0 до 1.0.</summary>
    float VadProbability { get; }

    /// <summary>true, если источник вообще способен предоставлять VAD (например, нативная библиотека загружена).</summary>
    bool IsAvailable { get; }
}
