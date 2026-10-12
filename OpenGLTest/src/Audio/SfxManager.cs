using System;
using System.Collections.Generic;
using NAudio.Wave;
using VoxelEngine.UI;

// One-shot playback opens a WinMM device, so the constructor refuses non-Windows up front and
// catches every remaining failure; Linux and headless runs stay silent like the ambience does.
#pragma warning disable CA1416

namespace VoxelEngine.Audio;

/// <summary>
/// One-shot sound effects built on the same NAudio stack as <see cref="AmbienceManager"/>:
/// every <c>.wav</c> under <c>assets/sfx</c> is decoded into memory at startup, grouped by
/// <see cref="SfxLibrary"/>, and <see cref="Play"/> mixes a random clip of a group into one
/// shared output. Never throws out of the constructor: an unreadable clip is skipped with a
/// warning, and a missing audio device leaves the game silent.
/// </summary>
public sealed class SfxManager : IDisposable
{
    private sealed class Group
    {
        public required short[][] Clips { get; init; }
    }

    private readonly Dictionary<string, Group> _groups = new(StringComparer.Ordinal);
    private readonly Random _random = new();
    private readonly SfxMixer? _mixer;
    private readonly WaveOutEvent? _output;
    private bool _disposed;

    /// <summary>False when no audio could be started; <see cref="Play"/> then does nothing.</summary>
    public bool Enabled { get; }

    public SfxManager(string sfxRoot = "assets/sfx")
    {
        if (!OperatingSystem.IsWindows())
        {
            Log.WriteLine("SFX disabled: audio playback is Windows-only.");
            return;
        }

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
                    _groups[name] = new Group { Clips = clips.ToArray() };
            }

            if (_groups.Count == 0)
            {
                Log.WriteLine($"SFX disabled: no playable .wav files under '{sfxRoot}'.");
                return;
            }

            _mixer = new SfxMixer();
            _output = new WaveOutEvent();
            _output.Init(_mixer);
            _output.Play();
            Enabled = true;
        }
        catch (Exception e)
        {
            _output?.Dispose();
            _mixer?.Dispose();
            _output = null;
            _mixer = null;
            Log.WriteLine($"SFX disabled: {e.Message}");
        }
    }

    /// <summary>Pushes the live SFX volume setting onto the mixer; call once per frame.</summary>
    public void Update()
    {
        if (_mixer != null)
            _mixer.Volume = GameSettings.SfxVolume;
    }

    /// <summary>
    /// Plays a uniformly random clip of <paramref name="group"/>. Unknown or empty groups are
    /// ignored, so a missing default is silent rather than fatal.
    /// </summary>
    public void Play(string group)
    {
        if (!Enabled || _disposed || _mixer == null) return;
        if (!_groups.TryGetValue(group, out var entry)) return;

        _mixer.Play(entry.Clips[PickIndex(entry.Clips.Length, _random)]);
    }

    /// <summary>
    /// A uniform pick over <paramref name="count"/> clips. Deliberately allows the same clip
    /// twice in a row: excluding the previous pick would force a two-variant group to alternate
    /// 1-2-1-2 rather than sound random.
    /// </summary>
    internal static int PickIndex(int count, Random random) => count <= 0 ? 0 : random.Next(count);

    private static bool TryLoad(string path, out short[] samples)
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
        _output?.Dispose();
        _mixer?.Dispose();
    }
}

