using System;
using System.Collections.Generic;
using System.Numerics;
using NAudio.Wave;
using VoxelEngine.UI;

namespace VoxelEngine.Audio;

/// <summary>
/// One-shot sound effects built on the same NAudio stack as <see cref="AmbienceManager"/>:
/// every <c>.wav</c> under <c>assets/sfx</c> is decoded into memory at startup, grouped by
/// <see cref="SfxLibrary"/>, and <see cref="Play"/> mixes a random clip of a group into one
/// shared <see cref="AudioOutput"/>, centered, while <see cref="PlayAt"/> plays its mono downmix
/// in 3D at a world position (<see cref="SpatialVoices"/>). Never throws out of the constructor: an
/// unreadable clip is skipped with a warning, and a missing audio device leaves the game silent.
/// </summary>
public sealed class SfxManager : IDisposable
{
    private sealed class Group
    {
        public required short[][] Clips { get; init; }
        // Each clip's mono downmix as a 3D voice buffer, by the same index
        public required uint[] Buffers { get; init; }
    }

    private readonly Dictionary<string, Group> _groups = new(StringComparer.Ordinal);
    private readonly Random _random = new();
    private readonly SfxMixer? _mixer;
    private readonly AudioOutput? _output;
    private readonly SpatialVoices? _voices;
    private Vector3 _listener;
    // The last usable right hand, kept while the view looks straight up or down
    private Vector3 _listenerRight = Vector3.UnitX;
    private bool _disposed;

    public SfxManager(string sfxRoot = "assets/sfx")
    {
        var loaded = new Dictionary<string, short[][]>(StringComparer.Ordinal);
        try
        {
            foreach (var (name, paths) in SfxLibrary.Scan(sfxRoot))
            {
                var clips = new List<short[]>(paths.Count);
                foreach (var path in paths)
                {
                    try
                    {
                        if (TryLoad(path, out var clip))
                            clips.Add(clip);
                        else
                            Log.Warn($"Skipping sfx '{path}': expected 16-bit stereo 44100 Hz PCM.");
                    }
                    catch (Exception e)
                    {
                        Log.Warn($"Skipping sfx '{path}': {e.Message}");
                    }
                }
                if (clips.Count > 0)
                    loaded[name] = clips.ToArray();
            }

            if (loaded.Count == 0)
            {
                Log.WriteLine($"SFX disabled: no playable .wav files under '{sfxRoot}'.");
                return;
            }

            // Before the first device user, so the device opens the way the setting asks
            AudioDevice.SetHrtf(GameSettings.Hrtf);
            _mixer = new SfxMixer();
            _output = new AudioOutput(_mixer);
            _voices = new SpatialVoices(AudioConfig.SfxVoices);
            foreach (var (name, clips) in loaded)
            {
                var buffers = new uint[clips.Length];
                for (var i = 0; i < clips.Length; i++)
                    buffers[i] = _voices.CreateBuffer(Downmix(clips[i]));
                _groups[name] = new Group { Clips = clips, Buffers = buffers };
            }
        }
        catch (Exception e)
        {
            _voices?.Dispose();
            _output?.Dispose();
            _mixer?.Dispose();
            _voices = null;
            _output = null;
            _mixer = null;
            _groups.Clear();
            Log.WriteLine($"SFX disabled: {e.Message}");
        }
    }

    /// <summary>Pushes the live SFX volume and 3D audio settings onto the output; call once per frame.</summary>
    public void Update()
    {
        if (_mixer == null) return;
        _mixer.Volume = GameSettings.SfxVolume;
        AudioDevice.SetHrtf(GameSettings.Hrtf);
    }

    /// <summary>
    /// Where positional sounds are heard from: the local player's eye and the unit direction it
    /// looks, pitch included, so a sound above or below is placed there. Call once per frame while a
    /// world plays.
    /// </summary>
    public void SetListener(Vector3 eye, Vector3 forward)
    {
        _listener = eye;
        var right = Vector3.Cross(forward, Vector3.UnitY);
        if (right.LengthSquared() > 1e-6f)
            _listenerRight = Vector3.Normalize(right);
        var up = Vector3.Normalize(Vector3.Cross(_listenerRight, forward));
        _voices?.SetListener(eye, forward, up);
    }

    /// <summary>
    /// Plays a uniformly random clip of <paramref name="group"/> centered at full volume: the local
    /// player's own sounds and the interface. Unknown or empty groups are ignored, so a missing
    /// default is silent rather than fatal.
    /// </summary>
    public void Play(string group)
    {
        if (_disposed || _mixer == null || !_groups.TryGetValue(group, out var entry)) return;
        _mixer.Play(entry.Clips[PickIndex(entry.Clips.Length, _random)]);
    }

    /// <summary>
    /// Plays a random clip of <paramref name="group"/> in 3D at <paramref name="position"/>, as heard
    /// from the listener (<see cref="SetListener"/>): its direction (left or right, in front or
    /// behind, above or below) from OpenAL, its loudness falling off with distance
    /// (<see cref="SoundFalloff"/>); nothing out of range.
    /// </summary>
    public void PlayAt(string group, Vector3 position)
    {
        if (_disposed || _voices == null || !_groups.TryGetValue(group, out var entry)) return;
        var gain = SoundFalloff.Gain(Vector3.Distance(_listener, position)) * GameSettings.SfxVolume;
        if (gain <= 0f) return;
        _voices.Play(entry.Buffers[PickIndex(entry.Buffers.Length, _random)], position, gain);
    }

    /// <summary>A stereo clip folded to mono (each frame's two channels averaged), what a 3D voice plays: OpenAL
    /// places only a mono sound in space.</summary>
    internal static short[] Downmix(short[] stereo)
    {
        var mono = new short[stereo.Length / 2];
        for (var i = 0; i < mono.Length; i++)
            mono[i] = (short)((stereo[2 * i] + stereo[2 * i + 1]) / 2);
        return mono;
    }

    /// <summary>
    /// A uniform pick over <paramref name="count"/> clips. Deliberately allows the same clip
    /// twice in a row: excluding the previous pick would force a two-variant group to alternate
    /// 1-2-1-2 rather than sound random.
    /// </summary>
    internal static int PickIndex(int count, Random random) => count <= 0 ? 0 : random.Next(count);

    /// <summary>
    /// Decodes <paramref name="path"/> into interleaved samples; false when it is not 16-bit stereo
    /// 44100 Hz PCM. Internal as a test seam, so the shipped clips can be proven loadable.
    /// </summary>
    internal static bool TryLoad(string path, out short[] samples)
    {
        samples = [];
        using var reader = new WaveFileReader(path);
        if (reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm
            || reader.WaveFormat.BitsPerSample != 16
            || reader.WaveFormat.Channels != SfxMixer.Channels
            || reader.WaveFormat.SampleRate != SfxMixer.SampleRate)
            return false;

        var bytes = new byte[checked((int)reader.Length)];
        var total = 0;
        while (total < bytes.Length)
        {
            var read = reader.Read(bytes, total, bytes.Length - total);
            if (read <= 0) break;
            total += read;
        }

        samples = new short[total / 2];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _voices?.Dispose();
        _output?.Dispose();
        _mixer?.Dispose();
    }
}

