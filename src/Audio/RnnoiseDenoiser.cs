using System;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;

namespace NeuroMicrophone.Audio;

/// <summary>
/// Обёртка над нативной библиотекой RNNoise (https://github.com/xiph/rnnoise,
/// BSD-3-Clause). RNNoise — рекуррентная нейросеть шумоподавления, работающая
/// строго кадрами по 480 сэмплов (10 мс) при частоте 48000 Гц, моно, со
/// значениями сэмплов в диапазоне 16-битного PCM (то есть примерно
/// [-32768, 32767], а не [-1.0, 1.0]) — поэтому перед вызовом нативной
/// функции сигнал масштабируется, а после — масштабируется обратно.
///
/// У самой библиотеки нет параметра "агрессивности подавления" — сила
/// подавления зашита в веса обученной модели. Регулируемая "агрессивность",
/// которую использует калибровка, реализована через WetMix: доля смешивания
/// шумоподавленного (wet) и исходного (dry) сигнала. Чем плотнее фоновый
/// шум, тем ближе WetMix к 1.0.
///
/// Если rnnoise.dll не найдена или несовместима по разрядности —
/// приложение не падает: IsAvailable становится false, и Read просто
/// пропускает сигнал без изменений (сухой проход).
/// </summary>
public sealed class RnnoiseDenoiser : ISampleProvider, IDisposable, IVoiceActivitySource
{
    private const int FrameSize = 480; // фиксировано форматом модели RNNoise: 10 мс при 48 кГц
    private const float Int16Scale = 32768f;

    private readonly ISampleProvider _source;
    private readonly IntPtr _state;
    private readonly bool _isAvailable;

    private readonly float[] _inputFrame = new float[FrameSize];
    private readonly float[] _outputFrame = new float[FrameSize];
    private readonly float[] _dryFrame = new float[FrameSize];
    private readonly float[] _pendingOutput = new float[FrameSize];
    private int _pendingCount;
    private int _pendingOffset;
    private float _vadProbability;

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>
    /// Вероятность голосовой активности для последнего обработанного 10-мс
    /// кадра (0.0 = точно шум, 1.0 = точно речь) — это фактическое возвращаемое
    /// значение rnnoise_process_frame, которое обычно отбрасывают. Используется
    /// шумовым гейтом (NoiseGate) как дополнительный, более "умный" сигнал
    /// открытия/закрытия — в отличие от простого порога по амплитуде, VAD не
    /// закрывается на тихих, но голосовых окончаниях слов.
    /// Если библиотека недоступна (IsAvailable == false), всегда равно 0.
    /// </summary>
    public float VadProbability => Volatile.Read(ref _vadProbability);

    /// <summary>
    /// 0 = полностью необработанный (сухой) сигнал;
    /// 1 = полностью шумоподавленный сигнал RNNoise.
    /// Устанавливается движком калибровки по плотности фонового шума.
    /// </summary>
    public float WetMix { get; set; } = 1f;

    public bool Enabled { get; set; } = true;

    /// <summary>true, если нативная библиотека rnnoise.dll успешно загружена.</summary>
    public bool IsAvailable => _isAvailable;

    public RnnoiseDenoiser(ISampleProvider source)
    {
        if (source.WaveFormat.Channels != 1 || source.WaveFormat.SampleRate != 48000)
        {
            throw new ArgumentException(
                "RnnoiseDenoiser требует моно поток с частотой дискретизации 48000 Гц на входе.",
                nameof(source));
        }

        _source = source;

        try
        {
            _state = RnnoiseNative.rnnoise_create(IntPtr.Zero);
            _isAvailable = _state != IntPtr.Zero;
        }
        catch (DllNotFoundException)
        {
            // rnnoise.dll не найдена рядом с исполняемым файлом — работаем без шумоподавления.
            _isAvailable = false;
            _state = IntPtr.Zero;
        }
        catch (BadImageFormatException)
        {
            // Найдена библиотека несовместимой разрядности (например, x86 вместо x64).
            _isAvailable = false;
            _state = IntPtr.Zero;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int written = 0;

        while (written < count)
        {
            if (_pendingCount == 0)
            {
                int samplesRead = _source.Read(_inputFrame, 0, FrameSize);
                if (samplesRead == 0)
                {
                    break; // источник временно исчерпан в рамках этого вызова
                }

                if (samplesRead < FrameSize)
                {
                    Array.Clear(_inputFrame, samplesRead, FrameSize - samplesRead);
                }

                ProcessFrame(samplesRead);
                _pendingCount = samplesRead;
                _pendingOffset = 0;
            }

            int toCopy = Math.Min(_pendingCount, count - written);
            Array.Copy(_pendingOutput, _pendingOffset, buffer, offset + written, toCopy);
            written += toCopy;
            _pendingOffset += toCopy;
            _pendingCount -= toCopy;
        }

        return written;
    }

    private void ProcessFrame(int validSamples)
    {
        if (!Enabled || !_isAvailable)
        {
            Array.Copy(_inputFrame, _pendingOutput, FrameSize);
            Volatile.Write(ref _vadProbability, 0f);
            return;
        }

        Array.Copy(_inputFrame, _dryFrame, FrameSize);

        // RNNoise ожидает диапазон, соответствующий 16-битному PCM.
        for (int i = 0; i < FrameSize; i++)
        {
            _inputFrame[i] *= Int16Scale;
        }

        float vad = RnnoiseNative.rnnoise_process_frame(_state, _outputFrame, _inputFrame);
        Volatile.Write(ref _vadProbability, vad);

        float wet = Math.Clamp(WetMix, 0f, 1f);
        for (int i = 0; i < FrameSize; i++)
        {
            float denoised = _outputFrame[i] / Int16Scale;
            _pendingOutput[i] = (denoised * wet) + (_dryFrame[i] * (1f - wet));
        }

        // Если исходный источник отдал меньше кадра (конец потока данных в этом вызове),
        // не оставляем "мусор" после последнего валидного сэмпла.
        if (validSamples < FrameSize)
        {
            Array.Clear(_pendingOutput, validSamples, FrameSize - validSamples);
        }
    }

    public void Dispose()
    {
        if (_state != IntPtr.Zero)
        {
            RnnoiseNative.rnnoise_destroy(_state);
        }
    }
}

/// <summary>
/// Точные сигнатуры нативного API RNNoise (rnnoise.h). Требует
/// rnnoise.dll (x64) рядом с исполняемым файлом — см. README.md.
/// </summary>
internal static class RnnoiseNative
{
    private const string LibraryName = "rnnoise";

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr rnnoise_create(IntPtr model);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void rnnoise_destroy(IntPtr state);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern float rnnoise_process_frame(IntPtr state, [Out] float[] output, [In] float[] input);
}
