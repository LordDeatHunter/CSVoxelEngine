namespace silkgltriangle;

/// <summary>
/// The GL-free decision behind the per-pass GPU timer queries: given whether the <c>X</c> profiler
/// overlay is up and the renderer's own frame counter, decide whether this frame issues its
/// <c>TIME_ELAPSED</c> queries at all. <c>RenderManager.BeginGpuFrame</c> latches the answer once
/// per frame and every <c>BeginGpuPass</c>/<c>EndGpuPass</c> inside that frame reads the latch.
///
/// The class is deliberately dependency-free — no GL, no <c>RenderManager</c>, no window — for the
/// same reason <c>ReflectionReuse</c> cites: <c>RenderManager</c> needs a live GL context to
/// construct and no test in the suite has one, so the only way to pin this policy headlessly is to
/// keep it out here. The duty cycle lives in
/// <see cref="WorldGenerationConfig.GpuTimingIdleInterval"/>.
///
/// <para>What the keep-alive preserves.</para>
/// The property worth keeping is not sample recency. <c>FrameProfiler</c> rebuilds its overlay rows
/// only inside <c>Publish</c>, once a second, clearing the GPU accumulators there — so what a player
/// sees the instant <c>X</c> is pressed is the <em>last published snapshot</em>, and the property is
/// that that standing snapshot already carries GPU figures instead of <c>--</c>. With one timed
/// frame in <see cref="WorldGenerationConfig.GpuTimingIdleInterval"/>, every publish window still
/// holds <c>ceil(fps / GpuTimingIdleInterval)</c> samples for each always-run pass — about 12 at
/// 100 fps — so the snapshot is populated by construction, at an eighth of the GL query traffic.
///
/// <para>What it costs.</para>
/// While the overlay is down an idle window's averages are built from roughly one frame in eight
/// instead of every frame, so the <c>~</c> worst-frame column under-samples: a max recorded during
/// an idle period can survive into the first window shown after <c>X</c>.
///
/// <para>The loss that actually bites.</para>
/// A pass that does not run every frame can go dark entirely. <c>SelectPass</c> is bracketed inside
/// <c>if (_hasSelectedBlock)</c> (World.cs:525-548); with the gate closed, the chance of a frame
/// both running that pass and being a timed frame falls by the interval, so it can collect zero
/// samples across a whole publish window and read <c>--</c> in the first snapshot after <c>X</c>
/// where an always-on build showed a figure. That is a real, partial loss of the very property this
/// gate is meant to preserve, and it is accepted rather than engineered around. If it is ever
/// judged unacceptable the alternative is an arming window — force a few fully timed frames on the
/// off-to-on transition — instead of a fixed duty cycle.
/// </summary>
internal static class GpuTimingGate
{
    /// <summary>
    /// True when this frame issues its per-pass <c>TIME_ELAPSED</c> queries: always while the
    /// overlay is up, otherwise on one frame in
    /// <see cref="WorldGenerationConfig.GpuTimingIdleInterval"/> as the keep-alive.
    /// </summary>
    /// <param name="overlayOn">The <c>X</c> profiler overlay is up this frame.</param>
    /// <param name="frameIndex">
    /// The renderer's own frame counter: monotonically increasing from 0, never reset and never
    /// negative. The only caller is <c>RenderManager</c>'s private field, so no wrap or sign dance
    /// is done here for a case that cannot occur.
    /// </param>
    internal static bool ShouldTime(bool overlayOn, long frameIndex)
    {
        if (overlayOn) return true;
        var interval = WorldGenerationConfig.GpuTimingIdleInterval;
        // <= 1 disables the gate entirely, which is also what keeps the modulo below safe.
        if (interval <= 1) return true;
        return frameIndex % interval == 0;
    }
}
