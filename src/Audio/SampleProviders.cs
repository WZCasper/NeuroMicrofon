using System;
using System.Threading;
using NAudio.Wave;

namespace NeuroMicrophone.Audio;

/// <summary>
/// Сводит многоканальный источник в моно усреднением каналов.
/// Вся внутренняя DSP-цепочка (шумоподавление, гейт, AGC, компрессор,
/// лимитер) работает в моно — это соответствует тому, что RNNoise
/// принимает ровно один канал.
/// </summary>
public sealed class MonoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _sourceChannels;
    private float[] _sourceBuffer = Array.Empty<float>();

    public WaveFormat WaveFormat { get; }

    public MonoSampleProvider(ISampleProvider source)
    {
        _source = source;
        _sourceChannels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_sourceChannels == 1)
        {
            return _source.Read(buffer, offset, count);
        }

        int sourceSamplesNeeded = count * _sourceChannels;
        if (_sourceBuffer.Length < sourceSamplesNeeded)
        {
            _sourceBuffer = new float[sourceSamplesNeeded];
        }

        int sourceSamplesRead = _source.Read(_sourceBuffer, 0, sourceSamplesNeeded);
        int framesRead = sourceSamplesRead / _sourceChannels;

        for (int frame = 0; frame < framesRead; frame++)
        {
            double sum = 0;
            int srcOffset = frame * _sourceChannels;
            for (int ch = 0; ch < _sourceChannels; ch++)
            {
                sum += _sourceBuffer[srcOffset + ch];
            }
            buffer[offset + frame] = (float)(sum / _sourceChannels);
        }

        return framesRead;
    }
}

/// <summary>
/// Дублирует моно-источник в N каналов (нужно перед выводом на устройство,
/// чей формат микширования — стерео/многоканальный, как это обычно бывает
/// у виртуальных аудиокабелей в общем режиме WASAPI).
/// </summary>
public sealed class ChannelExpanderSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _outputChannels;
    private float[] _monoScratch = Array.Empty<float>();

    public WaveFormat WaveFormat { get; }

    public ChannelExpanderSampleProvider(ISampleProvider monoSource, int outputChannels)
    {
        if (monoSource.WaveFormat.Channels != 1)
        {
            throw new ArgumentException("Источник для ChannelExpanderSampleProvider должен быть монофоническим.", nameof(monoSource));
        }

        _source = monoSource;
        _outputChannels = outputChannels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(monoSource.WaveFormat.SampleRate, outputChannels);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        // Контракт ISampleProvider.Read: count — это количество float-сэмплов
        // (не кадров), поэтому оно обязано быть кратно числу каналов формата,
        // который WaveFormat объявляет для этого провайдера. NAudio-стек
        // (WasapiOut, BufferedWaveProvider и т.п.) всегда соблюдает это на
        // практике, но проверка здесь превращает молчаливое, трудноуловимое
        // искажение звука (обрезанный последний кадр) в понятную ошибку сразу
        // в момент нарушения контракта, а не где-то в реальном воспроизведении.
        if (count % _outputChannels != 0)
        {
            throw new ArgumentException(
                $"count ({count}) должен быть кратен числу выходных каналов ({_outputChannels}).",
                nameof(count));
        }

        int framesRequested = count / _outputChannels;
        if (_monoScratch.Length < framesRequested)
        {
            _monoScratch = new float[framesRequested];
        }

        int framesRead = _source.Read(_monoScratch, 0, framesRequested);

        for (int frame = 0; frame < framesRead; frame++)
        {
            float sample = _monoScratch[frame];
            int dstOffset = offset + frame * _outputChannels;
            for (int ch = 0; ch < _outputChannels; ch++)
            {
                buffer[dstOffset + ch] = sample;
            }
        }

        // Возвращаем ровно столько float-сэмплов, сколько реально записали в
        // buffer (framesRead кадров по _outputChannels каналов каждый).
        // Мы намеренно НЕ трогаем и НЕ обнуляем хвост buffer после этой
        // границы: часть буфера за пределами возвращённого значения по
        // контракту ISampleProvider считается невалидной и не должна
        // читаться вызывающим кодом — так же ведут себя все стандартные
        // провайдеры NAudio. Обнуление задним числом создало бы иллюзию
        // валидных данных там, где их по контракту быть не должно.
        return framesRead * _outputChannels;
    }
}

/// <summary>
/// Прозрачная "врезка" в цепочку, которая на лету считает RMS/Peak
/// проходящего сигнала и публикует их в потокобезопасных полях
/// (Volatile.Read/Write), не блокируя аудиопоток и не выделяя память
/// на каждый вызов Read. UI-поток читает эти значения по таймеру.
/// </summary>
public sealed class MeteringSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private float _rmsDb = LevelMeter.MinDb;
    private float _peakDb = LevelMeter.MinDb;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public MeteringSampleProvider(ISampleProvider source)
    {
        _source = source;
    }

    public float RmsDb => Volatile.Read(ref _rmsDb);
    public float PeakDb => Volatile.Read(ref _peakDb);

    public int Read(float[] buffer, int offset, int count)
    {
        int samplesRead = _source.Read(buffer, offset, count);
        if (samplesRead > 0)
        {
            Volatile.Write(ref _rmsDb, LevelMeter.CalculateRmsDb(buffer, offset, samplesRead));
            Volatile.Write(ref _peakDb, LevelMeter.CalculatePeakDb(buffer, offset, samplesRead));
        }
        return samplesRead;
    }
}

/// <summary>
/// Заглушка микрофона. Продолжает читать из источника (чтобы огибающие
/// AGC/компрессора/лимитера не "застывали" и не давали щелчок при снятии
/// с мьюта), но обнуляет то, что реально уходит на выход.
/// </summary>
public sealed class MuteSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private volatile bool _isMuted;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public MuteSampleProvider(ISampleProvider source)
    {
        _source = source;
    }

    public bool IsMuted
    {
        get => _isMuted;
        set => _isMuted = value;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int samplesRead = _source.Read(buffer, offset, count);
        if (_isMuted && samplesRead > 0)
        {
            Array.Clear(buffer, offset, samplesRead);
        }
        return samplesRead;
    }
}
