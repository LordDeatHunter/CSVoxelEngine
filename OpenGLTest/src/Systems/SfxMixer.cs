using System;
using System.Collections.Generic;
using NAudio.Wave;

namespace silkgltriangle;

/// <summary>
/// Mixes any number of overlapping 16-bit stereo 44100 Hz clips over a silent baseline. All
/// clips share one format (validated at load), so no resampling is needed. A small lock
/// guards the active list against <see cref="Read"/>, which NAudio calls on its playback
/// thread while the game thread is free to start another one-shot.
/// </summary>
internal sealed class SfxMixer(float volume) : IWaveProvider, IDisposable
{
    internal const int SampleRate = 44100;
    internal const int Channels = 2;
    private const int BitsPerSample = 16;

    private sealed class ActiveClip
    {
        public required short[] Samples { get; init; }
        public int Position { get; set; }
    }

    private readonly object _gate = new();
    private readonly List<ActiveClip> _active = new();
    private readonly float _volume = Math.Clamp(volume, 0f, 1f);
    private int[] _scratch = new int[4096];

    public WaveFormat WaveFormat { get; } = new(SampleRate, BitsPerSample, Channels);

    public void Play(short[] samples)
    {
        lock (_gate)
            _active.Add(new ActiveClip { Samples = samples });
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        var totalSamples = count / 2;
        if (totalSamples == 0) return count;
        if (_scratch.Length < totalSamples)
            _scratch = new int[totalSamples];
        Array.Clear(_scratch, 0, totalSamples);

        lock (_gate)
        {
            for (var i = _active.Count - 1; i >= 0; i--)
            {
                var clip = _active[i];
                var available = clip.Samples.Length - clip.Position;
                var mix = Math.Min(totalSamples, available);
                for (var s = 0; s < mix; s++)
                    _scratch[s] += (int)(clip.Samples[clip.Position + s] * _volume);
                clip.Position += mix;
                if (clip.Position >= clip.Samples.Length)
                    _active.RemoveAt(i);
            }
        }

        for (var s = 0; s < totalSamples; s++)
        {
            var value = Math.Clamp(_scratch[s], short.MinValue, short.MaxValue);
            buffer[offset + s * 2] = (byte)(value & 0xFF);
            buffer[offset + s * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }
        return count;
    }

    public void Dispose()
    {
        lock (_gate)
            _active.Clear();
    }
}
