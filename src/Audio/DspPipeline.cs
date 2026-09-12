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
/// </summary>
public sealed class DspPipeline : ISampleProvider
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
}
