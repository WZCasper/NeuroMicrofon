namespace NeuroMicrophone.Models;

/// <summary>
/// Итог трёхэтапной 15-секундной калибровки: измеренные уровни и
/// рассчитанные на их основе параметры DSP-цепочки. Хранится отдельно
/// от DspPipeline, чтобы UI мог показать пользователю, что именно было
/// измерено и почему выбраны такие настройки.
/// </summary>
public sealed class CalibrationResult
{
    // --- Этап 1: фоновый шум ---
    public float NoiseFloorRmsDb { get; set; }
    public float NoiseFloorPeakDb { get; set; }
    public float GateThresholdDb { get; set; }
    public float DenoiserWetMix { get; set; }

    // --- Этап 2: обычная речь ---
    public float SpeechAverageRmsDb { get; set; }
    public float AgcTargetGainDb { get; set; }

    // --- Этап 3: громкая/эмоциональная речь ---
    public float LoudSpeechPeakDb { get; set; }
    public float CompressorThresholdDb { get; set; }
    public float CompressorRatio { get; set; }
    public float LimiterCeilingDb { get; set; }
}
