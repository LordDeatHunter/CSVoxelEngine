namespace VoxelEngine.Config;

/// <summary>
/// Main-thread simulation tuning: the fluid tick, tree felling, reactive tree growth, leaf decay,
/// ash fertilizer and the sapling clock.
/// </summary>
public static class SimulationConfig
{
    // Fluid simulation: one tick every FluidTickSeconds, at most MaxFluidTicksPerFrame of them
    // in a frame so a hitch never turns into a spiral of catch-up ticks
    public const float FluidTickSeconds = 0.25f;
    public const int MaxFluidTicksPerFrame = 4;

    // Cells one fluid tick processes per frame at most. A tick's cells are spread over the
    // frames until the next tick, so per-frame work stays flat while the simulation still keeps
    // up with a chunk-load wake storm (a per-tick cap alone leaves the storm queued, which
    // makes fluid appear frozen).
    public const int MaxFluidCellsPerFrame = 2048;

    // Ceiling on fluid cells queued for the next tick. Chunk wakes stop scheduling once it is
    // reached, so the queue can never grow past what the tick can work through.
    public const int MaxFluidBacklogCells = 8192;

    // Chunks whose worldgen water is woken per frame (FluidManager.WakeLoadedChunks). Each wake
    // schedules the chunk's fluid frontier and scans its six borders, so a streaming burst is
    // spread over frames; leftovers simply stay queued for the next one.
    public const int MaxWakeChunksPerFrame = 16;

    // How far a spreading fluid looks for a hole to fall into before spreading evenly
    public const int FluidSlopeSearchDistance = 4;

    // --- Tree felling (TopplingTree) ---
    // Seconds for a felled tree to swing from upright to flat (cubic ease-in: slow tip,
    // accelerating slam), then rest before its wood pops out as item drops
    public const float TreeToppleSeconds = 1.2f;
    public const float TreeFellRestSeconds = 2f;
    // Collection bounds: the wood cap covers a fell's nodes plus a wide trunk's shells and is
    // also the TreeSupport search cap (felling and the orphan crumble share it), then the
    // overall cap including the canopy. Sized to bound a mature wide trunk's nodes, shells and
    // fins and its canopy with room to spare. Anything past the caps stays standing (and
    // still drops normally when mined).
    public const int TreeFellWoodCap = 2048;
    public const int TreeFellTotalCap = 4096;

    /// <summary>How many of a felled tree's chunks (nearest to the feller first) remesh synchronously, so the standing tree vanishes the frame its falling copy appears; the rest rebuild through the dirty queue.</summary>
    public const int TreeFellSyncRemeshChunks = 8;

    // --- Reactive tree growth (TreeGrowthWriter) ---
    // Node cap of a grown tree (graph nodes only, never shells); it also bounds the
    // TreeGraph.FromWorld rebuild walk. Kept well under the fell's wood cap so a grown tree's
    // wood, shells included, still fells whole
    public const int TreeGrowNodeCap = 512;
    // Growth pulses one grow call may run at most
    public const int TreeGrowMaxPulsesPerCall = 256;

    // --- Leaf decay (LeafDecayManager) ---
    // Leaf decay simulation: one tick every LeafDecayTickSeconds, like the fluid tick, at most
    // MaxLeafDecayTicksPerFrame of them in a frame; leftover time is dropped so a hitch never
    // turns into a spiral of catch-up ticks
    public const float LeafDecayTickSeconds = 0.25f;
    public const int MaxLeafDecayTicksPerFrame = 2;
    // Leaf and tree-wood cells one decay tick processes per frame at most (the tick start drops
    // the air of a scheduled box with one read each). A tick's cells are walked in per-frame
    // slices until the next tick; a leaf scans its cluster's tip offsets best first, which stops
    // at the first live tip on an intact leaf but reads every offset on a doomed one, so this
    // stays well under the fluid budget.
    public const int MaxLeafDecayCellsPerFrame = 512;
    // Leaf decays plus orphaned-wood crumbles one tick may perform. The rest re-queue for the
    // next tick, so a ~300-leaf oak crown dissolves over ~8 ticks (about 2 s) instead of
    // vanishing in one frame.
    public const int LeafDecayMaxRemovalsPerTick = 40;
    // Ceiling on cells queued for the next decay tick, only a memory bound: ScheduleBox stops
    // adding once it is reached, and a stale leaf left behind waits for the next nearby edit.
    // It must hold a whole felled tree's box (the largest worldgen tree's reach on every side,
    // grown by the schedule reach, ~132k cells today for the stone pine and the palm) with room
    // to spare, or a fell would silently skip the far side of its crown.
    public const int MaxLeafDecayBacklogCells = 1 << 18;

    // --- Ash fertilizer (ItemUseAction.GrowTree): growth pulses one use of ash adds to the
    // aimed tree, spent from the stack ---
    public const int AshGrowthPulses = 3;

    // --- Saplings (SaplingManager): a planted sapling grows one pulse at a time until its
    // species' maturity step, one pulse per SaplingPulseSeconds jittered by SaplingPulseJitter ---
    public const float SaplingPulseSeconds = 3f;
    public const float SaplingPulseJitter = 0.5f;
}
