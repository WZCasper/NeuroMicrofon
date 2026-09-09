using System;
using NAudio.Wave;

namespace NeuroMicrophone.Audio;

/// <summary>
/// Прозрачная "врезка" в основную цепочку: каждый прочитанный буфер
/// одновременно (а) возвращается вызывающему коду как обычно (это
/// продолжает питать основной вывод на виртуальный кабель) и (б)
/// копируется в независимый BufferedWaveProvider, из которого отдельный,
/// асинхронно работающий WasapiOut может проигрывать тот же сигнал на
/// наушники/динамики пользователя ("Прослушать себя").
///
/// Это единственный корректный способ дать двум независимым WasapiOut
/// слушать один и тот же ISampleProvider: если бы оба выходных устройства
/// напрямую вызывали Read() на одном и том же объекте DspPipeline, они
/// бы боролись за один и тот же внутренний указатель позиции и портили
/// сигнал друг другу. BufferedWaveProvider же специально спроектирован
/// для развязки одного производителя и одного потребителя, работающих
/// с разной, не синхронизированной между собой скоростью чтения.
/// </summary>
public sealed class MonitorTapSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly BufferedWaveProvider _tapBuffer;
    private byte[] _byteScratch = Array.Empty<byte>();

    public WaveFormat WaveFormat => _source.WaveFormat;

    public MonitorTapSampleProvider(ISampleProvider source)
    {
        _source = source;
        _tapBuffer = new BufferedWaveProvider(source.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromMilliseconds(500),
        };
    }

    /// <summary>
    /// Создаёт независимый читающий конец врезки. Можно вызывать многократно
    /// (например, при каждом старте мониторинга) — каждый вызов оборачивает
    /// один и тот же внутренний буфер, но именно он должен пересоздаваться
    /// заново при каждом Init() нового WasapiOut.
    /// </summary>
    public ISampleProvider CreateMonitorReader() => _tapBuffer.ToSampleProvider();

    public int Read(float[] buffer, int offset, int count)
    {
        int samplesRead = _source.Read(buffer, offset, count);
        if (samplesRead > 0)
        {
            int byteCount = samplesRead * sizeof(float);
            if (_byteScratch.Length < byteCount)
            {
                _byteScratch = new byte[byteCount];
            }

            Buffer.BlockCopy(buffer, offset * sizeof(float), _byteScratch, 0, byteCount);
            _tapBuffer.AddSamples(_byteScratch, 0, byteCount);
        }

        return samplesRead;
    }
}
