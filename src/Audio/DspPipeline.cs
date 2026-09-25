using System;
using NAudio.Wave;

namespace NeuroMicrophone.Audio;

/// <summary>
/// Собирает полный аудиопайплайн строго в порядке, заданном спецификацией,
/// с добавленным по рекомендации фильтром верхних частот в самом начале:
///
///   [Микрофон] → HighPass → RNNoise → Noise Gate → AGC → Compressor → Limiter → [выход]
///
/// Каждая стадия остаётся публично доступной, чтобы CalibrationEngine и
/// MainViewModel могли читать/менять её параметры в реальном времени без
/// пересоздания всей цепочки.
///
/// Реализует IDisposable, потому что Denoiser (RnnoiseDenoiser) держит
/// нативный, неуправляемый указатель на состояние RNNoise (rnnoise_create).
/// Раньше DspPipeline не освобождался вообще: AudioEngine.Stop() просто
/// обнулял ссылку на пайплайн, и нативная память RNNoise никогда не
/// освобождалась — то есть каждое переключение микрофона/устройства вывода
/// или перезапуск движка приводил к утечке. Теперь Dispose() обязателен
/// к вызову перед тем, как отпустить ссылку на пайплайн (см. AudioEngine.Stop()).
/// </summary>
public sealed class DspPipeline : ISampleProvider, IDisposable
{
    public HighPassFilter HighPass { get; }
    public RnnoiseDenoiser Denoiser { get; }
    public NoiseGate Gate { get; }
    public AutoGainControl Agc { get; }
    public Compressor Comp { get; }
    public Limiter Lim { get; }

    private readonly ISampleProvider _finalStage;

    public WaveFormat WaveFormat => _finalStage.WaveFormat;

    public DspPipeline(ISampleProvider monoSource48k)
    {
        HighPass = new HighPassFilter(monoSource48k) { CutoffHz = 90f, Enabled = true };
        Denoiser = new RnnoiseDenoiser(HighPass);
        Gate = new NoiseGate(Denoiser, vadSource: Denoiser);
        Agc = new AutoGainControl(Gate);
        Comp = new Compressor(Agc);
        Lim = new Limiter(Comp);

        // Лимитер с потолком -2 dBFS включён по умолчанию всегда — это
        // требование "permanently prevent audio clipping" не должно зависеть
        // от того, прошла ли пользователь калибровку.
        Lim.Enabled = true;
        Lim.CeilingDb = -2f;

        _finalStage = Lim;
    }

    public int Read(float[] buffer, int offset, int count) => _finalStage.Read(buffer, offset, count);

    private bool _disposed;

    /// <summary>
    /// Освобождает нативное состояние RNNoise. Идемпотентен: повторный
    /// вызов безопасен и ничего не делает (тот же приём, что уже
    /// используется в HotkeyService/DeviceChangeNotifier).
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Denoiser.Dispose();
    }
}
