using System;
using NAudio.Wave;

namespace NeuroMicrophone.Tests;

/// <summary>Простой тестовый ISampleProvider, отдающий заранее заданный массив сэмплов.</summary>
internal sealed class ArraySampleProvider : ISampleProvider
{
    private readonly float[] _data;
    private int _position;

    public WaveFormat WaveFormat { get; }

    public ArraySampleProvider(float[] data, int sampleRate = 48000, int channels = 1)
    {
        _data = data;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int available = _data.Length - _position;
        int toCopy = Math.Min(available, count);
        Array.Copy(_data, _position, buffer, offset, toCopy);
        _position += toCopy;
        return toCopy;
    }
}
