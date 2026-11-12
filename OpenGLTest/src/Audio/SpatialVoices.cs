using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenAL;

namespace VoxelEngine.Audio;

/// <summary>
/// The positional one-shots: a fixed pool of OpenAL sources of the shared <see cref="AudioDevice"/>, each playing a
/// mono clip at a world position. OpenAL Soft renders them in true 3D against the listener's position and orientation
/// (<see cref="SetListener"/>): with HRTF on (<see cref="AudioDevice.SetHrtf"/>) a sound is placed in front, behind,
/// above or below the head over headphones, without it only panned between the speakers. The distance falloff is the
/// game's own (<see cref="SoundFalloff"/>, applied as each play's gain), so the context runs with no distance model.
/// A play with every source busy takes the one started longest ago. Main thread only; throws from the constructor when
/// no device opens.
/// </summary>
internal sealed unsafe class SpatialVoices : IDisposable
{
    private readonly AL _al;
    private readonly uint[] _sources;
    // When each source last started (a play counter), so the oldest is the one a full pool gives up
    private readonly long[] _startedAt;
    private readonly List<uint> _buffers = [];
    private long _plays;
    private bool _warned;
    private bool _disposed;

    public SpatialVoices(int count)
    {
        _al = AudioDevice.Acquire();
        try
        {
            _al.DistanceModel(DistanceModel.None);
            _sources = _al.GenSources(count);
            _startedAt = new long[count];
            var error = _al.GetError();
            if (error != AudioError.NoError)
                throw new InvalidOperationException($"OpenAL refused the 3D voices ({error})");
        }
        catch
        {
            AudioDevice.Release();
            throw;
        }
    }

    /// <summary>Uploads a mono 16-bit clip at <see cref="SfxMixer.SampleRate"/> as an OpenAL buffer to play from;
    /// it lives until <see cref="Dispose"/>.</summary>
    public uint CreateBuffer(short[] mono)
    {
        var buffer = _al.GenBuffer();
        fixed (short* data = mono)
            _al.BufferData(buffer, BufferFormat.Mono16, data, mono.Length * sizeof(short), SfxMixer.SampleRate);
        _buffers.Add(buffer);
        return buffer;
    }

    /// <summary>Where the sounds are heard from: the eye, the unit direction it looks and its unit up.</summary>
    public void SetListener(Vector3 eye, Vector3 forward, Vector3 up)
    {
        _al.SetListenerProperty(ListenerVector3.Position, eye.X, eye.Y, eye.Z);
        var orientation = stackalloc float[] { forward.X, forward.Y, forward.Z, up.X, up.Y, up.Z };
        _al.SetListenerProperty(ListenerFloatArray.Orientation, orientation);
    }

    /// <summary>Plays <paramref name="buffer"/> once at <paramref name="position"/> with <paramref name="gain"/>.</summary>
    public void Play(uint buffer, Vector3 position, float gain)
    {
        if (_disposed) return;
        var voice = PickVoice();
        var source = _sources[voice];
        _al.SourceStop(source);
        _al.SetSourceProperty(source, SourceInteger.Buffer, buffer);
        _al.SetSourceProperty(source, SourceVector3.Position, position.X, position.Y, position.Z);
        _al.SetSourceProperty(source, SourceFloat.Gain, MathUtils.Clamp01(gain));
        _al.SourcePlay(source);
        _startedAt[voice] = ++_plays;
        var error = _al.GetError();
        if (error == AudioError.NoError || _warned) return;
        _warned = true;
        Log.Warn($"Audio: OpenAL refused a 3D sound ({error}); further refusals stay quiet");
    }

    // A source that is not playing, else the one started longest ago
    private int PickVoice()
    {
        var oldest = 0;
        for (var i = 0; i < _sources.Length; i++)
        {
            _al.GetSourceProperty(_sources[i], GetSourceInteger.SourceState, out var state);
            if (state != (int)SourceState.Playing) return i;
            if (_startedAt[i] < _startedAt[oldest]) oldest = i;
        }
        return oldest;
    }

    /// <summary>Stops every voice and frees the sources, then the clip buffers (detached first, since OpenAL refuses
    /// to delete a buffer a source still holds).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var source in _sources)
        {
            _al.SourceStop(source);
            _al.SetSourceProperty(source, SourceInteger.Buffer, 0u);
        }
        _al.DeleteSources(_sources);
        if (_buffers.Count > 0) _al.DeleteBuffers(_buffers.ToArray());
        AudioDevice.Release();
    }
}
