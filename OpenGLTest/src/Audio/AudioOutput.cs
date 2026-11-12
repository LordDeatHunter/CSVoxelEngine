using System;
using System.Threading;
using NAudio.Wave;
using Silk.NET.OpenAL;

namespace VoxelEngine.Audio;

/// <summary>
/// Plays an <see cref="IWaveProvider"/> (16-bit PCM, mono or stereo) through one OpenAL source of
/// the shared <see cref="AudioDevice"/>. A background thread refills each queued buffer from the
/// provider as the source finishes it, so the provider is read on that thread, as NAudio's own
/// outputs read it on theirs. Throws from the constructor when no device opens; the managers turn
/// that into silence.
/// </summary>
internal sealed unsafe class AudioOutput : IDisposable
{
    private readonly IWaveProvider _provider;
    private readonly BufferFormat _format;
    private readonly byte[] _chunk;
    private readonly AL _al;
    private readonly uint _source;
    private readonly uint[] _buffers = [];
    private readonly Thread? _thread;
    private volatile bool _stopping;
    private bool _disposed;

    public AudioOutput(IWaveProvider provider)
    {
        var format = provider.WaveFormat;
        if (format.Encoding != WaveFormatEncoding.Pcm || format.BitsPerSample != 16 || format.Channels is not (1 or 2))
            throw new NotSupportedException($"audio output takes 16-bit mono or stereo PCM, not {format}");

        _provider = provider;
        _format = format.Channels == 1 ? BufferFormat.Mono16 : BufferFormat.Stereo16;
        _chunk = new byte[AudioConfig.OutputBufferFrames * format.BlockAlign];
        _al = AudioDevice.Acquire();
        try
        {
            _source = _al.GenSource();
            // Head-locked: the stream (the ambience, the centered sfx mix) never moves with the listener
            _al.SetSourceProperty(_source, SourceBoolean.SourceRelative, true);
            _al.SetSourceProperty(_source, SourceVector3.Position, 0f, 0f, 0f);
            _buffers = _al.GenBuffers(AudioConfig.OutputBufferCount);
            foreach (var buffer in _buffers) Fill(buffer);
            _al.SourceQueueBuffers(_source, _buffers);
            _al.SourcePlay(_source);
            var error = _al.GetError();
            if (error != AudioError.NoError)
                throw new InvalidOperationException($"OpenAL refused the stream ({error})");

            _thread = new Thread(Pump) { IsBackground = true, Name = "Audio output" };
            _thread.Start();
        }
        catch
        {
            FreeSource();
            throw;
        }
    }

    /// <summary>Refills and requeues every played buffer, restarting the source after an underrun.</summary>
    private void Pump()
    {
        try
        {
            while (!_stopping)
            {
                _al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out var processed);
                for (; processed > 0; processed--)
                {
                    uint buffer;
                    _al.SourceUnqueueBuffers(_source, 1, &buffer);
                    Fill(buffer);
                    _al.SourceQueueBuffers(_source, 1, &buffer);
                }

                // A source that ran dry stops itself; the refilled queue plays on.
                _al.GetSourceProperty(_source, GetSourceInteger.SourceState, out var state);
                if (state != (int)SourceState.Playing) _al.SourcePlay(_source);
                Thread.Sleep(AudioConfig.OutputPollMilliseconds);
            }
        }
        catch (Exception e)
        {
            Log.Warn($"Audio output stopped: {e.Message}");
        }
    }

    /// <summary>One buffer's worth of the provider, padded with silence when it runs short.</summary>
    private void Fill(uint buffer)
    {
        var total = 0;
        while (total < _chunk.Length)
        {
            var read = _provider.Read(_chunk, total, _chunk.Length - total);
            if (read <= 0) break;
            total += read;
        }

        Array.Clear(_chunk, total, _chunk.Length - total);
        fixed (byte* data = _chunk)
            _al.BufferData(buffer, _format, data, _chunk.Length, _provider.WaveFormat.SampleRate);
    }

    private void FreeSource()
    {
        _al.SourceStop(_source);
        _al.DeleteSource(_source);
        if (_buffers.Length > 0) _al.DeleteBuffers(_buffers);
        AudioDevice.Release();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopping = true;
        _thread?.Join();
        FreeSource();
    }
}
