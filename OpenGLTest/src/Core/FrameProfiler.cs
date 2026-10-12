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
    LeafDecay,
    Raycast,
    Breaking,
    ProgramUpdate,
    UsePlace,
    DebugLog,
    Player,
    Save,

    // Render side
    Lighting,
    Shadows,
    Reflection,
    LodPass,
    WorldPass,
    EntityPass,
    AoPass,
    WaterPass,
    CloudPass,
    RaysPass,
    GodraysPass,
    SelectPass,
    PostPass,
    UiPass,

    // Frame shell: work around the measured passes that used to hide inside "other"
    RenderShell,
    Screenshot,
    Publish,
}

/// <summary>
/// Per-frame CPU timing, collected on every frame, plus per-pass GPU timing, which is gated by
/// <see cref="GpuTimingGate"/>: it runs every frame while the overlay is up and on a keep-alive
/// frame while it is down, so the once-a-second snapshot standing when the overlay is switched on
/// already has data. Main thread only: <see cref="Begin"/>/<see cref="End"/> just read
/// <see cref="Stopwatch.GetTimestamp"/> into a flat array, and the only formatting — and so the
/// only allocation — happens on the once-a-second publish.
///
/// A section may be entered several times in one frame; its ticks accumulate. Sections must not
/// nest into themselves, since a single start slot is kept per section.
///
/// GPU time arrives per render pass from <see cref="RenderManager"/>'s consecutive
/// TIME_ELAPSED ranges (one query object per pass, drained a few frames late so it never
/// stalls). Update-side sections never get GPU samples and show "--" in that column. That timing
/// is gated: <see cref="RenderManager.BeginGpuFrame"/> latches
/// <see cref="GpuTimingGate.ShouldTime"/> once per frame, so while the overlay is down only one
/// frame in <see cref="WorldGenerationConfig.GpuTimingIdleInterval"/> issues its queries and each
/// always-run pass contributes about fps / GpuTimingIdleInterval samples to the window instead of
/// one per frame. Averages over an overlay-down window are therefore thinner and the worst-frame
/// column under-samples, and a pass that is not entered every frame (selectPass, bracketed by
/// World's <c>_hasSelectedBlock</c> test) can collect no samples at all across such a window and
/// read "--" in the first snapshot after the overlay comes up.
/// </summary>
public static class FrameProfiler
{
    // Short enough that a formatted row fits BitmapFont.MaxLayoutChars (64), parallel to ProfileSection
    private static readonly string[] Labels =
    [
        "camera", "chunkUnld", "chunkApply", "chunkDirty", "lod", "entities",
        "fluidWake", "fluidTick", "leafDecay", "raycast", "breaking",
        "progUpdate", "usePlace", "debugLog", "player", "save",
        "lighting", "shadows", "reflect", "lodPass", "worldPass", "entityPass", "aoPass", "waterPass",
        "cloudPass", "raysPass", "godPass", "selectPass", "postPass", "uiPass",
        "rendShell", "screenshot", "publish",
    ];

    private const string OtherLabel = "other";

    internal static readonly int SectionCount = Labels.Length;

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
    private static long _lodBuildCount;
    private static int _lodRegionCount;

    // Geometry free-list instrument (item D5), index order fixed 0=opaque, 1=translucent, 2=lod.
    // _geoFree/_geoPeak/_geoScanCum are the pools' own values as pushed by SetGeometryCounts;
    // the rest are derived here. _geoFreeWindowMax is a per-window maximum because the allocator's
    // lifetime peak is set during stream-in and then never moves, so it alone cannot answer
    // "mid-stream AND just after a teleport". _geoWorstFrameSplit keeps the worst frame's
    // three-way split so the LOD share of that frame reads straight off the log line.
    private static readonly int[] _geoFree = new int[3];
    private static readonly int[] _geoPeak = new int[3];
    private static readonly int[] _geoFreeWindowMax = new int[3];
    private static readonly long[] _geoScanCum = new long[3];
    private static readonly long[] _geoScanPrev = new long[3];
    private static readonly long[] _geoScanWindow = new long[3];
    private static long _geoWorstFrameSteps;
    private static readonly long[] _geoWorstFrameSplit = new long[3];

    // Scratch for the current frame's three deltas, static so EndFrame allocates nothing.
    private static readonly long[] _geoFrameSteps = new long[3];

    // Worldgen instrument: the shape- and batch-cache counters are lifetime, so each window's
    // delta is taken against the value seen at the previous publish
    private static long _shapeMissesPrev;
    private static long _batchMissesPrev;
    private static long _batchDuplicatesPrev;
    private static long _batchRotationsPrev;

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

    /// <summary>
    /// Header counts, pushed by the systems that own them (cheap int stores).
    /// <paramref name="lodBuilds"/> is <see cref="LodManager.CompletedBuildCount"/> — cumulative
    /// region builds — and <paramref name="lodRegions"/> the live region count; both are optional
    /// so callers that have no LOD to report (tests, any future call site) stay unchanged.
    /// They reach the console line only, never the HUD overlay: see <see cref="Publish"/>.
    /// </summary>
    public static void SetCounts(int chunks, int visible, long lodBuilds = 0, int lodRegions = 0)
    {
        _chunkCount = chunks;
        _visibleChunkCount = visible;
        _lodBuildCount = lodBuilds;
        _lodRegionCount = lodRegions;
    }

    /// <summary>
    /// Geometry free-list counts, pushed by the system that owns the three pools (cheap stores).
    /// Parameters arrive in the fixed order opaque, translucent, lod for each of the three
    /// quantities: current free-block count, lifetime peak free-block count, and cumulative
    /// allocator scan steps. Like <see cref="SetCounts"/> these reach the console line ONLY and
    /// never the HUD overlay: see <see cref="Publish"/>. <c>LargestFreeBlock</c> is deliberately
    /// absent — it is an O(n) getter and this runs every frame.
    /// </summary>
    public static void SetGeometryCounts(
        int freeOpaque, int freeTranslucent, int freeLod,
        int peakOpaque, int peakTranslucent, int peakLod,
        long scanOpaque, long scanTranslucent, long scanLod)
    {
        _geoFree[0] = freeOpaque;
        _geoFree[1] = freeTranslucent;
        _geoFree[2] = freeLod;
        _geoPeak[0] = peakOpaque;
        _geoPeak[1] = peakTranslucent;
        _geoPeak[2] = peakLod;
        _geoScanCum[0] = scanOpaque;
        _geoScanCum[1] = scanTranslucent;
        _geoScanCum[2] = scanLod;
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

        // Geometry free-list instrument: per-frame scan-step deltas off the pools' cumulative
        // counters, plus the window's worst frame with its three-way split and the window's
        // largest free-block count. Both unload paths (LodManager's retention loop and
        // ChunkManager.UnloadDistantChunks) are unbounded per frame, so a storm lives inside one
        // frame and an end-of-window sample would read the already-drained list.
        long geoFrameSteps = 0;
        for (var i = 0; i < 3; i++)
        {
            var delta = _geoScanCum[i] - _geoScanPrev[i];
            _geoScanPrev[i] = _geoScanCum[i];
            _geoScanWindow[i] += delta;
            _geoFrameSteps[i] = delta;
            geoFrameSteps += delta;
            if (_geoFree[i] > _geoFreeWindowMax[i]) _geoFreeWindowMax[i] = _geoFree[i];
        }

        if (geoFrameSteps > _geoWorstFrameSteps)
        {
            _geoWorstFrameSteps = geoFrameSteps;
            Array.Copy(_geoFrameSteps, _geoWorstFrameSplit, 3);
        }

        if (_accumWallTicks >= PublishIntervalTicks)
        {
            // Timed as its own section: the formatting runs inside EndFrame, so its cost lands
            // in the next frame's wall and would hide inside that frame's "other" untimed.
            Begin(ProfileSection.Publish);
            Publish();
            End(ProfileSection.Publish);
        }
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
        var gpuMaxMs = new double[SectionCount];
        var gpuSamples = new int[SectionCount];
        double gpuSumMs = 0;
        var gpuAny = false;
        for (var i = 0; i < SectionCount; i++)
        {
            if (_gpuSamples[i] == 0) continue;
            gpuAvgMs[i] = _gpuAccumNs[i] / (double)_gpuSamples[i] / 1_000_000.0;
            gpuMaxMs[i] = _gpuMaxNs[i] / 1_000_000.0;
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

        var wgenText = FormatWorldgen(seconds);

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
            $"chunks {_chunkCount}/{_visibleChunkCount} " +
            // LodGen throughput instrument: cumulative builds / live regions. Console line ONLY —
            // the overlay rows are fixed-width, and every gate screenshot passes hideHud:true while
            // perf-fly bakes the HUD in, so keeping this off _lines guarantees it cannot move a
            // gate pixel. The token is `regions`, never `lod`: `lod` is already a ProfileSection
            // label inside JoinSections and a scraper keyed on it would match the wrong field.
            $"regions {_lodBuildCount}/{_lodRegionCount} " +
            // Worldgen instrument (tree rework baseline): chunk generation, the tree stamp and the
            // shape builds, from WorldgenStats. Console line ONLY, like `regions`; `wgen` collides
            // with no Labels entry.
            wgenText +
            // Geometry free-list instrument (item D5), opaque/translucent/lod throughout: current
            // free blocks, this window's largest free-block count, the lifetime peak, this window's
            // scan steps, and the window's worst single frame with its three-way split. The raw
            // frame count is mandatory and not redundant with fps: fps is a rounded rate, and
            // _frames is zeroed below, so this is the only exact denominator for steps-per-frame.
            // Console line ONLY, for the same reason the `regions` token above is: the overlay rows
            // are fixed-width and perf-fly bakes the HUD into every gate screenshot, so a token that
            // never reaches _lines cannot move a gate pixel. The name `geo` collides with no Labels
            // entry, so a scraper keyed on it cannot match a JoinSections field.
            $"geo f {_geoFree[0]}/{_geoFree[1]}/{_geoFree[2]} " +
            $"pkw {_geoFreeWindowMax[0]}/{_geoFreeWindowMax[1]}/{_geoFreeWindowMax[2]} " +
            $"pk {_geoPeak[0]}/{_geoPeak[1]}/{_geoPeak[2]} " +
            $"scan {_geoScanWindow[0]}/{_geoScanWindow[1]}/{_geoScanWindow[2]} " +
            $"worst {_geoWorstFrameSteps}={_geoWorstFrameSplit[0]}/{_geoWorstFrameSplit[1]}/{_geoWorstFrameSplit[2]} " +
            $"frames {frames} | " +
            $"{JoinSections(frames, gpuAvgMs, gpuMaxMs, gpuSamples)}";
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
        // Window-scoped geometry state only. _geoScanPrev is NOT cleared — it tracks the pools'
        // cumulative counters across windows, and zeroing it would report every step ever taken as
        // one frame's delta. _geoPeak is NOT cleared either: the allocator owns that lifetime
        // high-water mark and simply re-pushes it every frame.
        Array.Clear(_geoScanWindow);
        Array.Clear(_geoFreeWindowMax);
        _geoWorstFrameSteps = 0;
        Array.Clear(_geoWorstFrameSplit);
    }

    // `wgen {chunks} {rate}/s {ms}ms/c trees {ms}/s {ms}/c grow {n} {ms}/s {ms}/t miss {n}
    // batch {n} dup {n} brot {n} tot ...`: this window's generated chunks, their mean
    // GenerateBlockData cost, the tree/cactus stamp's share per second and per chunk, the trees
    // grown (one entry per tree, solo or in a cell batch, on any thread), the SOLO shape-cache
    // misses, the cell-batch cache's misses, the batch builds that duplicated one already in
    // flight (GenerationalCache has no single-flight) and its generation rotations (thrash), then
    // the lifetime totals. Every ratio is 0 on an empty window.
    private static string FormatWorldgen(double seconds)
    {
        WorldgenStats.TakeWindow(out var treeTicks, out var genTicks, out var chunks,
            out var growTicks, out var grows);
        var misses = TreeGenerator.ShapeCacheMisses;
        var missDelta = misses - _shapeMissesPrev;
        _shapeMissesPrev = misses;
        var batchMisses = TreeGenerator.BatchCacheMisses;
        var batchMissDelta = batchMisses - _batchMissesPrev;
        _batchMissesPrev = batchMisses;
        var duplicates = TreeGenerator.BatchDuplicateBuilds;
        var dupDelta = duplicates - _batchDuplicatesPrev;
        _batchDuplicatesPrev = duplicates;
        var rotations = TreeGenerator.BatchCacheRotations;
        var rotDelta = rotations - _batchRotationsPrev;
        _batchRotationsPrev = rotations;

        var genMs = ToMilliseconds(genTicks);
        var treeMs = ToMilliseconds(treeTicks);
        var growMs = ToMilliseconds(growTicks);
        var chunkRate = seconds > 0 ? chunks / seconds : 0;
        var msPerChunk = chunks > 0 ? genMs / chunks : 0;
        var treeMsPerSecond = seconds > 0 ? treeMs / seconds : 0;
        var treeMsPerChunk = chunks > 0 ? treeMs / chunks : 0;
        var growMsPerSecond = seconds > 0 ? growMs / seconds : 0;
        var growMsPerTree = grows > 0 ? growMs / grows : 0;

        return $"wgen {chunks} {chunkRate:F1}/s {msPerChunk:F2}ms/c " +
               $"trees {treeMsPerSecond:F1}ms/s {treeMsPerChunk:F2}ms/c " +
               $"grow {grows} {growMsPerSecond:F1}ms/s {growMsPerTree:F3}ms/t " +
               $"miss {missDelta} batch {batchMissDelta} dup {dupDelta} brot {rotDelta} " +
               $"tot {WorldgenStats.TotalChunks} {ToMilliseconds(WorldgenStats.TotalGenTicks):F0}ms " +
               $"{ToMilliseconds(WorldgenStats.TotalTreeTicks):F0}ms {WorldgenStats.TotalGrows} " +
               $"{ToMilliseconds(WorldgenStats.TotalGrowTicks):F0}ms ";
    }

    // "label     avg   max   gpu" — 29 characters, inside DrawText's 32-glyph budget
    private static string FormatRow(string label, double avgMs, double maxMs, string gpuMs) =>
        $"{label,-10}{avgMs,6:F2}{maxMs,7:F2}{gpuMs,6}";

    // One section per `label avg~max`, with a `/gpuAvg~gpuMax` suffix where a render
    // pass reported GPU samples. `~` is the worst single frame in the publish window,
    // `/` separates the CPU pair from the GPU pair — the overlay's fixed-width rows
    // cannot fit the GPU max, so this console line is the record for spike hunting.
    private static string JoinSections(int frames, double[] gpuAvgMs, double[] gpuMaxMs, int[] gpuSamples)
    {
        var parts = new string[SectionCount];
        for (var i = 0; i < SectionCount; i++)
            parts[i] = gpuSamples[i] > 0
                ? $"{Labels[i]} {ToMilliseconds(_accumTicks[i]) / frames:F2}~{ToMilliseconds(_maxTicks[i]):F2}/{gpuAvgMs[i]:F2}~{gpuMaxMs[i]:F2}"
                : $"{Labels[i]} {ToMilliseconds(_accumTicks[i]) / frames:F2}~{ToMilliseconds(_maxTicks[i]):F2}";
        return string.Join(" | ", parts)
               + $" | {OtherLabel} {ToMilliseconds(_accumOtherTicks) / frames:F2}~{ToMilliseconds(_maxOtherTicks):F2}";
    }

    private static double ToMilliseconds(long ticks) => ticks / TicksPerMillisecond;
}
