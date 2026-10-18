using System;
using Silk.NET.OpenGL;

namespace VoxelEngine.Rendering;

/// <summary>
/// The per-pass <c>TIME_ELAPSED</c> query ring and its once-a-frame <see cref="GpuTimingGate"/>
/// latch: every render pass brackets itself with <see cref="BeginGpuPass"/>/<see cref="EndGpuPass"/>,
/// and the results drain into <see cref="FrameProfiler"/> a few frames late.
/// </summary>
internal sealed class GpuPassTimer : IDisposable
{
    private readonly GL _gl;

    // Per-pass GPU time: every render pass gets its own TIME_ELAPSED query object per
    // ring slot (see BeginGpuPass). GL forbids nesting TIME_ELAPSED, but consecutive
    // non-overlapping ranges are legal, so the passes are bracketed one after another and
    // each range lands in its own query. A result is only ever read when its slot comes
    // back around, a few frames late, so this never stalls. Three slots cover the usual
    // one-to-two frame latency. EntityPass runs in three segments per frame (the opaque entities,
    // the depth-less and blended ones after the contact-AO pass, then
    // bubbles after the water pass), hence up to MaxGpuSegments ranges per (pass, slot).
    private const int GpuRingSize = 3;
    private const int MaxGpuSegments = 3;
    // Every section, not just the render passes: UiPass is no longer the last enum value, and an
    // out-of-range index inside BeginGpuPass would fail silently, so size by the enum.
    private static readonly int GpuSectionCount = Enum.GetValues<ProfileSection>().Length;
    private readonly uint[,,] _gpuPassQueries = new uint[GpuSectionCount, GpuRingSize, MaxGpuSegments];
    private readonly bool[,,] _gpuPassPending = new bool[GpuSectionCount, GpuRingSize, MaxGpuSegments];
    private readonly int[] _gpuPassSegUsed = new int[GpuSectionCount];
    private int _gpuSlot;

    // R6's gate. _gpuTimingOn is latched once per frame in BeginGpuFrame and held for the whole
    // frame (see the comment there); it starts true so a frame that somehow ran before the first
    // BeginGpuFrame behaves exactly as it did before the gate existed. The three counters are the
    // gate's instrument: LogQueryCounts logs them once from RenderManager.Dispose, which is the only
    // place the gated configuration can report itself at all (Program.LogDebugLine returns early
    // when debug output is off, and that is the same flag the gate reads).
    private bool _gpuTimingOn = true;
    private long _gpuFrameIndex;
    private long _gpuTimedFrames;
    private long _gpuQueryCalls;

    internal GpuPassTimer(GL gl)
    {
        _gl = gl;

        for (var s = 0; s < GpuSectionCount; s++)
        for (var r = 0; r < GpuRingSize; r++)
        for (var g = 0; g < MaxGpuSegments; g++)
            _gpuPassQueries[s, r, g] = gl.GenQuery();
    }

    /// <summary>
    /// Prepares this frame's GPU timers: drains whatever per-pass results sit in the slot
    /// about to be reused and resets the per-pass segment counters. A query is only read
    /// when the driver says it is available, so this never stalls the pipeline; a result
    /// still missing after a full lap of the ring is simply dropped. Starts no query
    /// itself — the passes bracket their own ranges with
    /// <see cref="BeginGpuPass"/>/<see cref="EndGpuPass"/> further down this frame.
    /// <para>
    /// Also latches this frame's timing decision (<see cref="GpuTimingGate.ShouldTime"/>) into a
    /// field that is held for the whole frame, so every <see cref="BeginGpuPass"/> and
    /// <see cref="EndGpuPass"/> below reads one stable answer. While the overlay is down only one
    /// frame in <see cref="WorldGenerationConfig.GpuTimingIdleInterval"/> issues queries; the drain
    /// below runs regardless.
    /// </para>
    /// </summary>
    /// <param name="overlayOn">The <c>X</c> profiler overlay is up this frame.</param>
    internal void BeginGpuFrame(bool overlayOn)
    {
        // Latched once, here, and never re-read mid-frame: EndGpuPass advances _gpuPassSegUsed, so a
        // flip landing between a BeginGpuPass and its EndGpuPass would open a TIME_ELAPSED range that
        // is never closed and desync every later pass's segment index for the rest of the frame.
        _gpuTimingOn = GpuTimingGate.ShouldTime(overlayOn, _gpuFrameIndex);
        _gpuFrameIndex++;
        if (_gpuTimingOn) _gpuTimedFrames++;

        // The drain stays UNCONDITIONAL whatever the gate says, and that is load-bearing: a
        // _gpuPassPending entry written while the gate was open must still be cleared after the gate
        // closes, or that slot is reused with a stale pending flag forever. It also means a
        // gate-closed frame is not necessarily query-free — with GpuRingSize == 3 the queries issued on
        // a timed frame k are drained on frame k+3, which is itself a gate-closed frame.
        for (var s = 0; s < GpuSectionCount; s++)
        for (var g = 0; g < MaxGpuSegments; g++)
        {
            if (!_gpuPassPending[s, _gpuSlot, g]) continue;
            _gpuPassPending[s, _gpuSlot, g] = false;

            _gl.GetQueryObject(_gpuPassQueries[s, _gpuSlot, g],
                QueryObjectParameterName.ResultAvailable, out int available);
            _gpuQueryCalls++;
            if (available == 0) continue;

            _gl.GetQueryObject(_gpuPassQueries[s, _gpuSlot, g],
                QueryObjectParameterName.Result, out long nanoseconds);
            _gpuQueryCalls++;
            FrameProfiler.ReportGpuPass((ProfileSection)s, nanoseconds);
        }

        Array.Clear(_gpuPassSegUsed);
    }

    /// <summary>
    /// Opens one pass's GPU timer. Ranges must be consecutive and non-overlapping: every
    /// <see cref="BeginGpuPass"/> needs its <see cref="EndGpuPass"/> before the next pass
    /// begins, since GL forbids nesting TIME_ELAPSED. A pass with several draw blocks per
    /// frame (EntityPass) simply brackets each block; each lands in its own query object
    /// and the samples add up in the overlay average.
    /// <para>
    /// No-ops on a frame the gate closed. That decision is latched once per frame in
    /// <see cref="BeginGpuFrame"/> and held for the whole frame, never re-read here: were it read
    /// per call, a toggle landing between this and its <see cref="EndGpuPass"/> would open a
    /// TIME_ELAPSED range nothing ever closes and desync every later pass's segment index.
    /// </para>
    /// </summary>
    internal void BeginGpuPass(ProfileSection section)
    {
        if (!_gpuTimingOn) return;
        var s = (int)section;
        var seg = _gpuPassSegUsed[s];
        if (seg >= MaxGpuSegments) return;
        _gl.BeginQuery(QueryTarget.TimeElapsed, _gpuPassQueries[s, _gpuSlot, seg]);
        _gpuQueryCalls++;
        _gpuPassPending[s, _gpuSlot, seg] = true;
    }

    /// <summary>
    /// Closes the range opened by <see cref="BeginGpuPass"/>. Reads the same per-frame latch, for
    /// the same reason: the gate is decided once in <see cref="BeginGpuFrame"/> and held, so a
    /// bracket that opened this frame is always the bracket that closes it.
    /// </summary>
    internal void EndGpuPass(ProfileSection section)
    {
        if (!_gpuTimingOn) return;
        var s = (int)section;
        if (_gpuPassSegUsed[s] >= MaxGpuSegments) return;
        _gl.EndQuery(QueryTarget.TimeElapsed);
        _gpuQueryCalls++;
        _gpuPassSegUsed[s]++;
    }

    /// <summary>Moves the query ring on; the drained slot is reused two frames later.</summary>
    internal void EndGpuFrame()
    {
        _gpuSlot = (_gpuSlot + 1) % GpuRingSize;
    }

    /// <summary>Logs the gate's query counters once; <see cref="RenderManager.Dispose"/> calls it first.</summary>
    internal void LogQueryCounts()
    {
        // R6's counted instrument, and the reason it is a shutdown log line rather than a token on
        // FrameProfiler's console row: Program.LogDebugLine returns early when debug output is off,
        // and that is the very flag the gate reads, so a console token could only ever report the
        // always-on configuration. This prints in every run, gated or not — the RenderManager
        // constructor's one "GPU:" line is the precedent — and Program.OnClosing disposes before
        // Log.Shutdown.
        Log.WriteLine($"GPU timer queries: {_gpuQueryCalls} GL calls over {_gpuFrameIndex} frames, " +
                      $"{_gpuTimedFrames} timed ({(_gpuFrameIndex > 0 ? _gpuQueryCalls / (double)_gpuFrameIndex : 0):F1} per frame)");
    }

    public void Dispose()
    {
        foreach (var query in _gpuPassQueries)
            _gl.DeleteQuery(query);
    }
}
