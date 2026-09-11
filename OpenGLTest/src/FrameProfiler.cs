using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace silkgltriangle;

/// <summary>
/// The part of a frame a timing sample belongs to. The order here is the order the rows
/// appear in the overlay, and it must match <see cref="FrameProfiler"/>'s label table.
/// </summary>
public enum ProfileSection
{
    // Update side
    Camera,
    ChunkUnload,
    ChunkApply,
    ChunkDirty,
    Lod,
    Entities,
    FluidWake,
    FluidTick,
    Raycast,
    Breaking,

    // Render side
    Lighting,
    Shadows,
    Reflection,
    LodPass,
    WorldPass,
    EntityPass,
    WaterPass,
    CloudPass,
    RaysPass,
    SelectPass,
    UiPass
}

/// <summary>
/// Per-frame CPU (and per-pass GPU) timing, always collected so the overlay has data the
/// moment it is switched on. Main thread only: <see cref="Begin"/>/<see cref="End"/> just read
/// <see cref="Stopwatch.GetTimestamp"/> into a flat array, and the only formatting — and so the
/// only allocation — happens on the once-a-second publish.
///
/// A section may be entered several times in one frame; its ticks accumulate. Sections must not
/// nest into themselves, since a single start slot is kept per section.
///
/// GPU time arrives per render pass from <see cref="RenderManager"/>'s consecutive
/// TIME_ELAPSED ranges (one query object per pass, drained a few frames late so it never
/// stalls). Update-side sections never get GPU samples and show "--" in that column.
/// </summary>
public static class FrameProfiler
{
    // Short enough for the 32-character cap in UIRenderer.DrawText, parallel to ProfileSection
    private static readonly string[] Labels =
    [
        "camera", "chunkUnld", "chunkApply", "chunkDirty", "lod", "entities",
        "fluidWake", "fluidTick", "raycast", "breaking",
        "lighting", "shadows", "reflect", "lodPass", "worldPass", "entityPass", "waterPass", "cloudPass",
        "raysPass", "selectPass", "uiPass"
    ];

    private const string OtherLabel = "other";

    private static readonly int SectionCount = Labels.Length;

    // Per-frame scratch, cleared by EndFrame
    private static readonly long[] _startTicks = new long[Labels.Length];
    private static readonly long[] _frameTicks = new long[Labels.Length];

    // Accumulated over the publish window
    private static readonly long[] _accumTicks = new long[Labels.Length];
    private static readonly long[] _maxTicks = new long[Labels.Length];

    private static long _lastFrameEnd;
    private static long _accumWallTicks;
    private static long _accumOtherTicks;
    private static long _maxOtherTicks;
    private static int _frames;

    // Per-section GPU time in nanoseconds, fed by ReportGpuPass and cleared on publish.
    // Only render-side sections ever get samples; the rest keep zeros and render as "--".
    private static readonly long[] _gpuAccumNs = new long[Labels.Length];
    private static readonly long[] _gpuMaxNs = new long[Labels.Length];
    private static readonly int[] _gpuSamples = new int[Labels.Length];

    private static int _gcBase0;
    private static int _gcBase1;
    private static int _gcBase2;
    private static bool _gcBaseTaken;

    private static int _chunkCount;
    private static int _visibleChunkCount;

    private static readonly List<string> _lines = [];
    private static string _consoleLine = string.Empty;
    private static bool _consolePending;

    private static readonly double TicksPerMillisecond = Stopwatch.Frequency / 1000.0;
    private static readonly long PublishIntervalTicks = Stopwatch.Frequency;

    /// <summary>
    /// The published snapshot: header rows followed by one row per section, each already
    /// formatted and under 32 characters. Rebuilt once a second, so drawing it costs nothing.
    /// </summary>
    public static IReadOnlyList<string> Lines => _lines;

    /// <summary>The same snapshot squashed onto one line, for the console log.</summary>
    public static string ConsoleLine => _consoleLine;

    /// <summary>Starts timing <paramref name="section"/> in the current frame.</summary>
    public static void Begin(ProfileSection section) =>
        _startTicks[(int)section] = Stopwatch.GetTimestamp();

    /// <summary>Adds the elapsed time since the matching <see cref="Begin"/> to this frame.</summary>
    public static void End(ProfileSection section)
    {
        var index = (int)section;
        _frameTicks[index] += Stopwatch.GetTimestamp() - _startTicks[index];
    }

    /// <summary>Header counts, pushed by the systems that own them (cheap int stores).</summary>
    public static void SetCounts(int chunks, int visible)
    {
        _chunkCount = chunks;
        _visibleChunkCount = visible;
    }

    /// <summary>
    /// Feeds one completed per-pass GPU TIME_ELAPSED result (see
    /// <see cref="RenderManager.BeginGpuPass"/>). Results arrive a few frames late and some
    /// are dropped, so they are simply averaged over the publish window.
    /// </summary>
    public static void ReportGpuPass(ProfileSection section, long nanoseconds)
    {
        var index = (int)section;
        _gpuAccumNs[index] += nanoseconds;
        if (nanoseconds > _gpuMaxNs[index]) _gpuMaxNs[index] = nanoseconds;
        _gpuSamples[index]++;
    }

    /// <summary>
    /// Hands out <see cref="ConsoleLine"/> exactly once per published snapshot, so the caller
    /// can log it a second at a time without keeping its own timer.
    /// </summary>
    public static bool TryTakeSnapshotForConsole(out string line)
    {
        if (!_consolePending)
        {
            line = string.Empty;
            return false;
        }

        _consolePending = false;
        line = _consoleLine;
        return true;
    }

    /// <summary>
    /// Closes the frame: folds its per-section ticks into the window, records the wall time
    /// since the previous call (which includes the buffer swap, so wall minus the sections is
    /// the "other" row) and publishes a fresh snapshot every second. Call once, last, in the
    /// render callback.
    /// </summary>
    public static void EndFrame()
    {
        var now = Stopwatch.GetTimestamp();

        // The very first frame has no previous boundary to measure from
        var wall = _lastFrameEnd == 0 ? 0 : now - _lastFrameEnd;
        _lastFrameEnd = now;

        long sum = 0;
        for (var i = 0; i < SectionCount; i++)
        {
            var ticks = _frameTicks[i];
            _frameTicks[i] = 0;
            _accumTicks[i] += ticks;
            if (ticks > _maxTicks[i]) _maxTicks[i] = ticks;
            sum += ticks;
        }

        // Clamped: a section straddling the frame boundary can leave the difference negative
        var other = wall - sum;
        if (other < 0) other = 0;

        _accumWallTicks += wall;
        _accumOtherTicks += other;
        if (other > _maxOtherTicks) _maxOtherTicks = other;
        _frames++;

        if (_accumWallTicks >= PublishIntervalTicks)
            Publish();
    }

    private static void Publish()
    {
        var frames = Math.Max(1, _frames);
        var seconds = _accumWallTicks / (double)Stopwatch.Frequency;

        var frameMs = ToMilliseconds(_accumWallTicks) / frames;
        var fps = seconds > 0 ? (int)Math.Round(frames / seconds) : 0;

        long cpuTicks = 0;
        for (var i = 0; i < SectionCount; i++)
            cpuTicks += _accumTicks[i];
        var cpuMs = ToMilliseconds(cpuTicks) / frames;

        var gpuAvgMs = new double[SectionCount];
        var gpuSamples = new int[SectionCount];
        double gpuSumMs = 0;
        var gpuAny = false;
        for (var i = 0; i < SectionCount; i++)
        {
            if (_gpuSamples[i] == 0) continue;
            gpuAvgMs[i] = _gpuAccumNs[i] / (double)_gpuSamples[i] / 1_000_000.0;
            gpuSamples[i] = _gpuSamples[i];
            gpuSumMs += gpuAvgMs[i];
            gpuAny = true;
        }

        var gpuText = gpuAny ? gpuSumMs.ToString("F2") : "--";

        // Deltas, so the counts read as "collections in the last second"
        var gc0 = GC.CollectionCount(0);
        var gc1 = GC.CollectionCount(1);
        var gc2 = GC.CollectionCount(2);
        if (!_gcBaseTaken)
        {
            _gcBase0 = gc0;
            _gcBase1 = gc1;
            _gcBase2 = gc2;
            _gcBaseTaken = true;
        }

        _lines.Clear();
        _lines.Add($"frame {frameMs,5:F2} ms  {fps,3} fps");
        _lines.Add($"cpu {cpuMs,5:F2}  gpu {gpuText,6}");
        _lines.Add($"gc {gc0 - _gcBase0}/{gc1 - _gcBase1}/{gc2 - _gcBase2} per s");
        _lines.Add($"chunks {_chunkCount} vis {_visibleChunkCount}");

        for (var i = 0; i < SectionCount; i++)
            _lines.Add(FormatRow(Labels[i], ToMilliseconds(_accumTicks[i]) / frames,
                ToMilliseconds(_maxTicks[i]),
                gpuSamples[i] > 0 ? gpuAvgMs[i].ToString("F2") : "--"));

        _lines.Add(FormatRow(OtherLabel, ToMilliseconds(_accumOtherTicks) / frames,
            ToMilliseconds(_maxOtherTicks), "--"));

        _consoleLine =
            $"perf frame {frameMs:F2}ms {fps}fps cpu {cpuMs:F2} gpu {gpuText} " +
            $"gc {gc0 - _gcBase0}/{gc1 - _gcBase1}/{gc2 - _gcBase2} " +
            $"chunks {_chunkCount}/{_visibleChunkCount} | {JoinSections(frames, gpuAvgMs, gpuSamples)}";
        _consolePending = true;

        _gcBase0 = gc0;
        _gcBase1 = gc1;
        _gcBase2 = gc2;

        Array.Clear(_accumTicks);
        Array.Clear(_maxTicks);
        _accumWallTicks = 0;
        _accumOtherTicks = 0;
        _maxOtherTicks = 0;
        _frames = 0;
        Array.Clear(_gpuAccumNs);
        Array.Clear(_gpuMaxNs);
        Array.Clear(_gpuSamples);
    }

    // "label     avg   max   gpu" — 29 characters, inside DrawText's 32-glyph budget
    private static string FormatRow(string label, double avgMs, double maxMs, string gpuMs) =>
        $"{label,-10}{avgMs,6:F2}{maxMs,7:F2}{gpuMs,6}";

    private static string JoinSections(int frames, double[] gpuAvgMs, int[] gpuSamples)
    {
        var parts = new string[SectionCount];
        for (var i = 0; i < SectionCount; i++)
            parts[i] = gpuSamples[i] > 0
                ? $"{Labels[i]} {ToMilliseconds(_accumTicks[i]) / frames:F2}/{gpuAvgMs[i]:F2}"
                : $"{Labels[i]} {ToMilliseconds(_accumTicks[i]) / frames:F2}";
        return string.Join(" | ", parts)
               + $" | {OtherLabel} {ToMilliseconds(_accumOtherTicks) / frames:F2}";
    }

    private static double ToMilliseconds(long ticks) => ticks / TicksPerMillisecond;
}
