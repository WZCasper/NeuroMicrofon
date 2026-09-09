using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NeuroMicrophone.Models;
using NeuroMicrophone.Services;

namespace NeuroMicrophone.Audio;

/// <summary>
/// Связывает воедино захват с физического микрофона (WASAPI, общий режим,
/// буфер 20 мс — что укладывается в требование "менее 20 мс"), полную
/// DSP-цепочку и вывод на виртуальное аудиоустройство.
///
/// Схема прохождения сигнала:
///   WasapiCapture → BufferedWaveProvider → Mono → Resample(48кГц) → [raw meter]
///     → DspPipeline (Denoise → Gate → AGC → Compressor → Limiter) → [processed meter]
///     → Mute → адаптация к формату устройства вывода (ресемплинг/расширение каналов)
///     → WasapiOut
///
/// "raw meter" используется CalibrationEngine для честного измерения
/// характеристик исходного сигнала микрофона, "processed meter" — тем, что
/// видит пользователь на VU-метре (обработанный, "мокрый" сигнал).
/// </summary>
public sealed class AudioEngine : IDisposable
{
    public const int InternalSampleRate = 48000;

    private WasapiCapture? _capture;
    private WasapiOut? _output;
    private BufferedWaveProvider? _bufferedWaveProvider;

    private MeteringSampleProvider? _rawMeter;
    private DspPipeline? _dsp;
    private MeteringSampleProvider? _processedMeter;
    private MuteSampleProvider? _muteStage;
    private MonitorTapSampleProvider? _monitorTap;
    private WasapiOut? _monitorOutput;

    private DeviceChangeNotifier? _captureNotifier;
    private DeviceChangeNotifier? _renderNotifier;

    public bool IsRunning { get; private set; }

    /// <summary>Сообщения об ошибках устройства (отключение микрофона/виртуального кабеля и т.п.).</summary>
    public event EventHandler<string>? ErrorOccurred;

    public DspPipeline? Pipeline => _dsp;

    public float RawRmsDb => _rawMeter?.RmsDb ?? LevelMeter.MinDb;
    public float RawPeakDb => _rawMeter?.PeakDb ?? LevelMeter.MinDb;
    public float ProcessedRmsDb => _processedMeter?.RmsDb ?? LevelMeter.MinDb;
    public float ProcessedPeakDb => _processedMeter?.PeakDb ?? LevelMeter.MinDb;

    public bool IsMuted
    {
        get => _muteStage?.IsMuted ?? false;
        set
        {
            if (_muteStage != null) _muteStage.IsMuted = value;
        }
    }

    /// <summary>Идёт ли сейчас "прослушивание себя" через отдельное устройство.</summary>
    public bool IsMonitoring => _monitorOutput != null;

    public static IEnumerable<AudioDeviceInfo> GetInputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device)
            {
                yield return new AudioDeviceInfo(device.ID, device.FriendlyName);
            }
        }
    }

    public static IEnumerable<AudioDeviceInfo> GetOutputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                yield return new AudioDeviceInfo(device.ID, device.FriendlyName);
            }
        }
    }

    public void Start(string inputDeviceId, string outputDeviceId)
    {
        Stop();

        using var enumerator = new MMDeviceEnumerator();
        MMDevice captureDevice = enumerator.GetDevice(inputDeviceId);
        MMDevice renderDevice = enumerator.GetDevice(outputDeviceId);

        try
        {
            // Буфер 20 мс в общем режиме WASAPI — компромисс между низкой
            // задержкой (требование "< 20 мс") и устойчивостью к подгрузкам CPU.
            _capture = new WasapiCapture(captureDevice, useEventSyncContext: false, audioBufferMillisecondsLength: 20);
            _capture.DataAvailable += OnCaptureDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;

            _bufferedWaveProvider = new BufferedWaveProvider(_capture.WaveFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromMilliseconds(500),
            };

            ISampleProvider captureSampleProvider = _bufferedWaveProvider.ToSampleProvider();

            ISampleProvider monoProvider = captureSampleProvider.WaveFormat.Channels == 1
                ? captureSampleProvider
                : new MonoSampleProvider(captureSampleProvider);

            ISampleProvider resampledProvider = monoProvider.WaveFormat.SampleRate == InternalSampleRate
                ? monoProvider
                : new WdlResamplingSampleProvider(monoProvider, InternalSampleRate);

            _rawMeter = new MeteringSampleProvider(resampledProvider);
            _dsp = new DspPipeline(_rawMeter);
            _processedMeter = new MeteringSampleProvider(_dsp);
            _muteStage = new MuteSampleProvider(_processedMeter);
            _monitorTap = new MonitorTapSampleProvider(_muteStage);

            ISampleProvider outputStage = AdaptToDeviceFormat(_monitorTap, renderDevice);

            _output = new WasapiOut(renderDevice, AudioClientShareMode.Shared, useEventSync: true, latency: 20);
            _output.Init(outputStage);
            _output.PlaybackStopped += OnPlaybackStopped;

            _capture.StartRecording();
            _output.Play();

            _captureNotifier = new DeviceChangeNotifier(inputDeviceId);
            _captureNotifier.DeviceRemovedOrDisabled += msg => ErrorOccurred?.Invoke(this, $"Микрофон: {msg}");

            _renderNotifier = new DeviceChangeNotifier(outputDeviceId);
            _renderNotifier.DeviceRemovedOrDisabled += msg => ErrorOccurred?.Invoke(this, $"Виртуальное устройство: {msg}");

            IsRunning = true;
        }
        catch (Exception ex)
        {
            IsRunning = false;
            ErrorOccurred?.Invoke(this, $"Не удалось запустить аудиодвижок: {ex.Message}");
            Stop();
            throw;
        }
        finally
        {
            captureDevice.Dispose();
            renderDevice.Dispose();
        }
    }

    /// <summary>
    /// Приводит моно-сигнал 48 кГц к фактическому формату микширования
    /// целевого устройства (частота дискретизации и число каналов) — это
    /// необходимо для общего режима WASAPI, который требует точного
    /// совпадения формата с MixFormat устройства. Используется как для
    /// основного вывода на виртуальный кабель, так и для мониторингового
    /// вывода — у них могут быть разные форматы микширования.
    /// </summary>
    private static ISampleProvider AdaptToDeviceFormat(ISampleProvider monoSource, MMDevice device)
    {
        WaveFormat deviceFormat = device.AudioClient.MixFormat;
        ISampleProvider stage = monoSource;

        if (stage.WaveFormat.SampleRate != deviceFormat.SampleRate)
        {
            stage = new WdlResamplingSampleProvider(stage, deviceFormat.SampleRate);
        }

        if (deviceFormat.Channels > 1)
        {
            stage = new ChannelExpanderSampleProvider(stage, deviceFormat.Channels);
        }

        return stage;
    }

    /// <summary>
    /// Запускает "прослушивание себя" — дублирует уже обработанный сигнал
    /// (после Mute, то есть ровно то, что слышат собеседники) на указанное
    /// устройство, независимо от основного вывода на виртуальный кабель.
    /// Требует, чтобы движок уже был запущен через Start().
    /// </summary>
    public void StartMonitoring(string monitorDeviceId)
    {
        StopMonitoring();

        if (_monitorTap == null)
        {
            throw new InvalidOperationException("Аудиодвижок не запущен — сначала вызовите Start().");
        }

        using var enumerator = new MMDeviceEnumerator();
        MMDevice monitorDevice = enumerator.GetDevice(monitorDeviceId);

        try
        {
            ISampleProvider monitorReader = _monitorTap.CreateMonitorReader();
            ISampleProvider adapted = AdaptToDeviceFormat(monitorReader, monitorDevice);

            _monitorOutput = new WasapiOut(monitorDevice, AudioClientShareMode.Shared, useEventSync: true, latency: 40);
            _monitorOutput.Init(adapted);
            _monitorOutput.Play();
        }
        catch (Exception ex)
        {
            _monitorOutput?.Dispose();
            _monitorOutput = null;
            ErrorOccurred?.Invoke(this, $"Не удалось запустить прослушивание: {ex.Message}");
            throw;
        }
        finally
        {
            monitorDevice.Dispose();
        }
    }

    public void StopMonitoring()
    {
        if (_monitorOutput != null)
        {
            try { _monitorOutput.Stop(); } catch (Exception) { /* устройство могло уже исчезнуть */ }
            _monitorOutput.Dispose();
            _monitorOutput = null;
        }
    }

    private void OnCaptureDataAvailable(object? sender, WaveInEventArgs e)
    {
        _bufferedWaveProvider?.AddSamples(e.Buffer, 0, e.BytesRecorded);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            ErrorOccurred?.Invoke(this, $"Микрофон отключён или произошла ошибка захвата: {e.Exception.Message}");
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            ErrorOccurred?.Invoke(this, $"Виртуальное устройство отключено или произошла ошибка вывода: {e.Exception.Message}");
        }
    }

    public void Stop()
    {
        IsRunning = false;

        StopMonitoring();

        _captureNotifier?.Dispose();
        _captureNotifier = null;
        _renderNotifier?.Dispose();
        _renderNotifier = null;

        if (_capture != null)
        {
            _capture.DataAvailable -= OnCaptureDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            try { _capture.StopRecording(); } catch (Exception) { /* устройство могло уже исчезнуть */ }
            _capture.Dispose();
            _capture = null;
        }

        if (_output != null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            try { _output.Stop(); } catch (Exception) { /* устройство могло уже исчезнуть */ }
            _output.Dispose();
            _output = null;
        }

        _bufferedWaveProvider = null;
        _rawMeter = null;
        _dsp = null;
        _processedMeter = null;
        _muteStage = null;
        _monitorTap = null;
    }

    public void Dispose()
    {
        Stop();
    }
}
