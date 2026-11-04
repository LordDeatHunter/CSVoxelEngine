namespace VoxelEngine.Config;

/// <summary>
/// Chunk and far-LOD streaming tuning: the view radius, per-frame upload, dirty and scan budgets,
/// thread counts, LOD build and upload budgets, and the loading phase's budgets and gate.
/// </summary>
public static class StreamingConfig
{
    // View distance
    public const int ChunkRadius = 16;
    public const int ChunkRadiusSq = ChunkRadius * ChunkRadius;

    // How many completed meshes to upload to GPU per frame
    public const int MaxMeshUploadsPerFrame = 32;

    // How many dirty chunks to process per frame (kick off mesh gen)
    public const int MaxDirtyChunksPerFrame = 64;

    // How many dirty-set entries one frame scans for mesh candidates. The set holds
    // thousands of entries mid-stream, so an uncapped scan is milliseconds by itself;
    // leftovers simply wait for the next frames (item 19).
    public const int MaxDirtyScanPerFrame = 1024;

    // How many mesh generation tasks can run concurrently on the thread pool
    public const int MaxConcurrentMeshGenerations = 4;

    // How many dedicated threads for block data generation
    public const int MaxConcurrentBlockDataGenerations = 4;

    // How many dedicated threads build LOD region meshes (LodWorkerPool's LodGen pool).
    //
    // SHIPS AT 1: a pool was built, measured and declined. The thread census said 3 (12 hardware
    // threads on the reference machine, minus 1 render thread, minus MaxConcurrentMeshGenerations
    // = 4 ThreadPool mesh runners at the pool's default Normal priority, minus
    // MaxConcurrentBlockDataGenerations = 4 dedicated BelowNormal chunk-gen threads), and the
    // capture said otherwise: three BelowNormal workers reach a fixed region count 1.31x sooner
    // but slow chunk generation DISJOINTLY (chunks loaded at the 4th perf line 11156-12485
    // baseline against 8427-10253, p = 0.0143; seconds to the full 17077-chunk ring 5.0 -> 6.1)
    // and cost 30 % of burst frame rate (median 108.0 -> 75.3 fps); two workers clear the
    // chunk-gen and GC guards but still cost 18 % (110.3 -> 90.7) for 1.26x. Decision gate 2's
    // branch 4 applies -- neither size clears -- so the pool goes and everything around it stays.
    //
    // Why so far below linear, and the reason a bigger pool must not be re-proposed: a LOD build
    // is terrain generation in disguise. LodRegion.BuildMeshData prefetches 36 / 100 / 324 chunk
    // columns at levels 0 / 1 / 2 through ColumnCache.GetHeights, and a column that misses
    // Chunk.ColumnCache is generated on the spot (measured medians 17.7 / 17.6 / 65.3 ms per
    // build, tracking the column count, not the mesh). The four chunk-gen threads are doing that
    // same work on that same cache at that same moment, so LodGen workers do not add throughput to
    // an idle resource -- they contend for the one the burst already saturates. The lever the
    // measurement pointed at was cutting the work, not more threads, and that lever HAS NOW BEEN
    // TAKEN (item D2, landed): the cap is unchanged at 16384 entries, but the eviction is no
    // longer a wholesale Clear(). ColumnCache runs on GenerationalCache, so a level-2 ring sweep
    // (roughly 51k distinct columns) can only age out the OLDEST generation at a time instead of
    // dropping chunk generation's hot working set along with its own one-shot columns (that set is
    // a radius-16 disc: ~800 columns, 933 once each build's 3x3 heightmap neighborhood is counted).
    // Measured on the replayed key sequences: chunk-gen's own misses during the mixed burst fell
    // 1219 -> 733.
    //
    // Everything the pool needed is still here and still correct at N = 1 -- the TryAdd-gated
    // _building claim with both rollbacks, MaxLodBuildsInFlight, the Idle/Throttled split and the
    // raised MeshBufferPool ceilings -- so raising this constant is a one-token experiment. The
    // priority claim, if it is: the workers run at BelowNormal, so they cannot PREEMPT the
    // Normal-priority render thread or the mesh runners. That claim is deliberately narrow because
    // the stronger one is false. Priority bounds preemption only, and two channels still reach the
    // frame regardless of who allocated: (1) GC -- a MeshBufferPool miss falls through to a fresh
    // ~786 KB List<Vertex>(LodRegion.MeshVertexCapacity) on the large object heap, and Workstation
    // concurrent GC suspends the main thread whichever thread allocated; (2) shared last-level
    // cache and memory bandwidth, which is the channel the measurement above actually caught
    // (item C5).
    public const int MaxConcurrentLodGenerations = 1;

    // Delay when no chunks need generating (ms)
    public const int GenerationDelayIdleMs = 200;

    // --- Far-terrain LOD ---
    // Level definitions (cell sizes, per-level radii) live in LodRegion.Levels.

    // How many completed LOD region meshes to upload to GPU per frame. This is the MAIN
    // THREAD's per-frame GL ceiling (VAO setup, buffer uploads) and C5 does NOT raise it. It is
    // also nowhere near the limiter: C5's capture measured the single builder producing 53-55
    // regions/s against this cap's 4 x 108 fps = ~433 regions/s, i.e. 7.8-8.2x headroom, which is
    // why MaxConcurrentLodGenerations could be raised at all. MaxConcurrentLodGenerations builders
    // would exceed this cap during the streaming burst -- that is what MaxLodBuildsInFlight bounds
    // -- but the pool ships at 1, so today the drain stays ahead of the builder. Halving it from 8
    // cost no convergence (item 26).
    public const int MaxLodUploadsPerFrame = 4;

    // How many finished region builds may wait for the main thread before the LodGen workers
    // stop claiming. Outstanding rents on LodManager.MeshBuffers are therefore bounded by
    // MaxLodBuildsInFlight (waiting) + MaxConcurrentLodGenerations (building), which is what the
    // pool's retention ceilings are sized from. 8 is two frames of uploads at
    // MaxLodUploadsPerFrame = 4, so a worker can run a frame ahead of the drain without the
    // queue turning into an unbounded backlog of pinned vertex lists (item C5).
    public const int MaxLodBuildsInFlight = 8;

    // How long a THROTTLED LodGen worker backs off, in ms. A throttled worker is not idle: work
    // is available and it is only waiting for the main thread to drain the completed queue, so it
    // must not sleep GenerationDelayIdleMs = 200 (that would stall the burst for a fifth of a
    // second at a time). This is a COARSE backoff whose real granularity is the OS timer: a 1 ms
    // Thread.Sleep on Windows lands anywhere from ~1 to ~15 ms unless the timer resolution was
    // raised, so it is not "about a frame" and nothing promises it is. Correctness does not depend
    // on the value at all -- the next spin simply re-checks the cap and retries (item C5).
    public const int LodBuildThrottleDelayMs = 1;

    // How many unburied level-0 regions one buried-flag pass cover-tests. Level-0 tests
    // walk 16 columns of chunk lookups each, so the uncapped pass is ~10 ms mid-stream;
    // new regions, coarser levels and still-buried regions are always tested (item 19).
    public const int MaxLodCoverTestsPerFrame = 64;

    // Cells a removal of a column's top block scans down for the new LOD surface before giving up.
    public const int LodSurfaceRescanDepth = 64;
    // Chunks around the camera that must be generated and meshed before a loaded world starts
    // playing; the sphere is ~123 chunks, kept far below ChunkRadius so meshing always has its neighbors.
    public const int LoadReadyRadiusChunks = 3;
    // A loading world starts playing anyway after this much accumulated update time.
    public const double LoadTimeoutSeconds = 30.0;
    // The per-frame caps while loading: larger than play, still bounded so no frame spikes.
    public const int LoadingMeshUploadsPerFrame = 64;
    public const int LoadingDirtyChunksPerFrame = 128;
    public const int LoadingLodUploadsPerFrame = 8;
}
