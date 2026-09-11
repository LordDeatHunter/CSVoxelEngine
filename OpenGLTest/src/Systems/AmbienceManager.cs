using System;
using System.IO;
using NAudio.Wave;
using NLayer.NAudioSupport;

// WinMM playback is Windows-only by design; the constructor refuses non-Windows up front
// and catches every remaining failure, so Linux and headless runs stay silent. MP3s decode
// through managed NLayer rather than AudioFileReader, whose Windows build is only picked
// when the app itself targets a -windows TFM.
#pragma warning disable CA1416

namespace silkgltriangle;

/// <summary>
/// Looping day/night ambience with a slow crossfade. Volumes track the lighting pass's
/// dayness (0 at night, 1 in full day), so dawn and dusk fade one loop out while fading
/// the other in. Never throws out of the constructor: a missing file, missing codec or
/// missing audio device logs one warning and leaves the game silent.
/// </summary>
public sealed class AmbienceManager : IDisposable
{
    private readonly Voice? _day;
    private readonly Voice? _night;
    private float _dayVolume;
    private float _nightVolume;
    private float _startupSeconds;
    private bool _disposed;

    /// <summary>False when the loops could not start; Update then does nothing.</summary>
    public bool Enabled { get; }

    public float DayVolume => _dayVolume;
    public float NightVolume => _nightVolume;

    public AmbienceManager(
        string dayPath = WorldGenerationConfig.DayAmbiencePath,
        string nightPath = WorldGenerationConfig.NightAmbiencePath)
    {
        Voice? day = null;
        Voice? night = null;
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.WriteLine("Ambience disabled: audio playback is Windows-only.");
                return;
            }
            if (!File.Exists(dayPath) || !File.Exists(nightPath))
            {
                Console.WriteLine($"Ambience disabled: missing '{dayPath}' or '{nightPath}'.");
                return;
            }
            day = new Voice(dayPath);
            night = new Voice(nightPath);
            _day = day;
            _night = night;
            Enabled = true;
        }
        catch (Exception e)
        {
            Console.WriteLine($"Ambience disabled: {e.Message}");
            night?.Dispose();
            day?.Dispose();
        }
    }

    /// <summary>
    /// Crossfade targets for a dayness value: the day loop owns the day share of the
    /// master volume and the night loop owns the rest, so their sum stays constant.
    /// </summary>
    public static (float Day, float Night) ComputeTargetVolumes(float dayness, float master)
    {
        var d = MathUtils.Clamp01(dayness);
        return (d * master, (1f - d) * master);
    }

    /// <summary>
    /// Builds the looping playback chain for one file: managed MP3 decode, the seamless
    /// repeat, then a volume stage. The repeat must sit below the volume stage because
    /// that stage pads end-of-stream with silence instead of reporting it. Headless-safe
    /// (touches no audio device), so tests cover it.
    /// </summary>
    internal static (Voice voice, WaveChannel32 volume) CreateLoopSource(string path)
    {
        var voice = new Voice(path, startPlaying: false);
        return (voice, voice.Volume);
    }

    /// <summary>0 at launch, 1 once the startup fade has played out.</summary>
    internal static float StartupScale(float elapsedSeconds) =>
        MathUtils.SmoothStep(0f, WorldGenerationConfig.AmbienceStartupFadeSeconds, elapsedSeconds);

    /// <summary>
    /// Eases both loop volumes toward their crossfade targets. The startup fade holds the
    /// master down for the first seconds, so launch rises gradually instead of jumping in.
    /// </summary>
    public void Update(float dayness, float deltaTime)
    {
        if (!Enabled || _disposed)
            return;
        _startupSeconds = Math.Min(_startupSeconds + Math.Max(0f, deltaTime),
            WorldGenerationConfig.AmbienceStartupFadeSeconds);
        var master = GameSettings.AmbienceVolume * StartupScale(_startupSeconds);
        var (dayTarget, nightTarget) = ComputeTargetVolumes(dayness, master);
        _dayVolume = MathUtils.Damp(_dayVolume, dayTarget,
            WorldGenerationConfig.AmbienceFadeRate, deltaTime);
        _nightVolume = MathUtils.Damp(_nightVolume, nightTarget,
            WorldGenerationConfig.AmbienceFadeRate, deltaTime);
        if (MathF.Abs(_dayVolume - dayTarget) < 0.001f)
            _dayVolume = dayTarget;
        if (MathF.Abs(_nightVolume - nightTarget) < 0.001f)
            _nightVolume = nightTarget;
        _day!.Volume.Volume = _dayVolume;
        _night!.Volume.Volume = _nightVolume;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _day?.Dispose();
        _night?.Dispose();
    }

    /// <summary>
    /// One looping voice: managed MP3 decode, the seamless repeat, a volume stage, output.
    /// Skipping playback keeps construction headless-safe for the decode test.
    /// </summary>
    internal sealed class Voice : IDisposable
    {
        public WaveChannel32 Volume { get; }
        public long SourceLength => _source.Length;
        private readonly ManagedMpegStream _source;
        private readonly LoopStream _loop;
        private readonly WaveOutEvent? _output;

        public Voice(string path, bool startPlaying = true)
        {
            ManagedMpegStream? source = null;
            LoopStream? loop = null;
            WaveChannel32? volume = null;
            try
            {
                source = new ManagedMpegStream(path);
                loop = new LoopStream(source);
                volume = new WaveChannel32(loop) { Volume = 0f };
                _source = source;
                _loop = loop;
                Volume = volume;
                if (startPlaying)
                    _output = StartLoop(volume);
            }
            catch
            {
                volume?.Dispose();
                loop?.Dispose();
                source?.Dispose();
                throw;
            }
        }

        public int ReadLoop(byte[] buffer, int offset, int count) =>
            _loop.Read(buffer, offset, count);

        private static WaveOutEvent StartLoop(WaveStream source)
        {
            var output = new WaveOutEvent();
            try
            {
                output.Init(source);
                output.Play();
                return output;
            }
            catch
            {
                output.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _output?.Dispose();
            _loop.Dispose();
            Volume.Dispose();
            _source.Dispose();
        }
    }

    /// <summary>Repeats its source forever; the standard NAudio looping wrapper.</summary>
    private sealed class LoopStream(WaveStream source) : WaveStream
    {
        private const int MaxLoopResetsPerRead = 8;

        public override WaveFormat WaveFormat => source.WaveFormat;
        public override long Length => source.Length;

        public override long Position
        {
            get => source.Position;
            set => source.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var totalBytesRead = 0;
            var resets = 0;
            while (totalBytesRead < count)
            {
                var bytesRead = source.Read(buffer, offset + totalBytesRead, count - totalBytesRead);
                if (bytesRead == 0)
                {
                    if (source.Position == 0 || ++resets > MaxLoopResetsPerRead)
                        break;
                    source.Position = 0;
                }
                totalBytesRead += bytesRead;
            }
            return totalBytesRead;
        }
    }
}
