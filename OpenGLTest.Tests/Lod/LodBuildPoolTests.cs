using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using VoxelEngine.Data;
using VoxelEngine.Lod;
using VoxelEngine.Player;
using VoxelEngine.Rendering;
using Xunit;

namespace VoxelEngine.Tests.Lod;

/// <summary>
/// The stress half of item C5's test pairing: <see cref="StressWorkers"/> real builder threads
/// driving <see cref="LodManager.TryBuildOneRegion"/> against one drain, the shape a pooled
/// production run would have. The width is fixed at 3 on purpose and is NOT read from
/// <see cref="WorldGenerationConfig.MaxConcurrentLodGenerations"/>, which C5's measurement left at
/// 1: sizing these threads from the shipped constant would defuse every fact here into a
/// single-threaded run that still passes (see <see cref="StressWorkers"/>). The equivalence half — the claim protocol pinned deterministically on one
/// thread — lives in <c>LodManagerTests</c>'s C5 section.
///
/// <para><b>What a passing concurrency test proves, and what it does not.</b> These facts
/// demonstrate the absence of the races they happen to exercise, on this machine, under this
/// scheduler, for this many iterations and this deadline. They are not a proof that the design is
/// race-free. That argument is the set of invariants written on <see cref="LodManager"/> itself —
/// the TryAdd-gated <c>_building</c> registration taken INSIDE each claim path, the mandatory
/// <c>_queued</c> rollback in <see cref="LodManager.FindClosestNeededRegion"/>, the in-flight cap
/// checked BEFORE any claim is taken, and the release ordered AFTER
/// <see cref="LodManager.EnqueueCompletedMesh"/> — and these facts are a check on that argument,
/// never a substitute for it. A genuinely racy variant that simply never interleaves badly here
/// would pass every one of them. A future round must not upgrade repeated green runs into evidence:
/// if the protocol is changed, the argument has to be re-made, and the deterministic facts in
/// <c>LodManagerTests</c> (each mutation-verified) are what fail loudly when it is weakened.</para>
///
/// <para>This file was run five times consecutively, green each time, before the round moved on.
/// That is worth exactly what the paragraph above says it is worth and no more. Two source
/// mutations were run to check the facts can actually fail, each reverted byte-exactly afterwards:
/// moving <see cref="LodManager.FindClosestNeededRegion"/>'s <c>_queued</c> rollback onto the
/// winning path fails <see cref="ThreeWorkersBetweenThemClaimEveryRegionExactlyOnce"/>, and deleting
/// the in-flight cap from <see cref="LodManager.TryBuildOneRegion"/> fails
/// <see cref="TheInFlightCapHoldsUnderThreeProducers"/> with an observed queue depth of 29 against
/// the bound of 11. A green concurrency fact is worth nothing unless the fact can fail.</para>
///
/// <para>Conventions, from <c>ChunkScanFrontierTests</c>: fixed <see cref="Thread"/> arrays rather
/// than <c>Task.Run</c> or <c>Parallel.For</c> (the real workers are dedicated threads, and the
/// ThreadPool would silently serialise or inject threads of its own), <c>IsBackground = true</c> and
/// a name per thread, a <see cref="Stopwatch"/> deadline rather than a fixed iteration count, and
/// <c>Join()</c> on every thread before any assertion is read. Nothing is asserted on a worker
/// thread — an xUnit assertion that throws there is swallowed and the fact would pass silently — so
/// every worker funnels failures into a <see cref="ConcurrentBag{T}"/> the test thread asserts on.
/// None of these tests calls <see cref="LodManager.Start"/>: that would spawn the production pool
/// alongside the test's own threads and measure two pools at once.</para>
/// </summary>
[Collection("LOD column caps")]
public sealed class LodBuildPoolTests
{
    public LodBuildPoolTests() => TestContent.EnsureLoaded();

    /// <summary>
    /// The stress width, deliberately FIXED rather than read from
    /// <see cref="WorldGenerationConfig.MaxConcurrentLodGenerations"/>. That constant is a
    /// measured product decision and currently ships at 1; if these threads were sized from it,
    /// every fact in this file would quietly become a single-threaded run that still passed, with
    /// names still promising three workers and the concurrency coverage the C5 safety argument
    /// rests on simply gone — and no failing test to say so. The claim protocol is written for N
    /// workers whatever N production picks, so it is exercised at 3 here regardless. Raise, never
    /// lower.
    /// </summary>
    private const int StressWorkers = 3;

    /// <summary>The LodManagerTests C5 idiom, copied rather than shared: those helpers are private
    /// statics on that class and no shared test-helper type exists beyond <c>TestContent</c>.</summary>
    private static LodManager ClaimableManager()
    {
        var lods = new LodManager(null!);
        var cam = new Camera(null, null);
        cam.TestTeleport(Vector3.Zero, 0f, 0f);
        lods.AttachCamera(cam);
        return lods;
    }

    /// <summary>
    /// Bit-exact list comparison. <see cref="Vertex"/> is a 32-byte <c>Pack = 1</c> struct of
    /// blittable fields with no padding and no overridden <c>Equals</c>, so <c>SequenceEqual</c>
    /// would fall back to reflective <c>ValueType.Equals</c> per element (the bitwise fast path is
    /// refused for types carrying floats) — tens of thousands of reflective compares per build,
    /// across four threads, for the whole deadline. Comparing the backing spans as bytes is the
    /// same question asked exactly and cheaply.
    /// </summary>
    private static bool BytesEqual<T>(List<T> a, List<T> b) where T : struct =>
        a.Count == b.Count &&
        MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(a))
            .SequenceEqual(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(b)));

    private static bool BytesEqual<T>(T[] a, T[] b) where T : struct =>
        a.Length == b.Length &&
        MemoryMarshal.AsBytes(new ReadOnlySpan<T>(a)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(b)));

    private static string? DescribeMismatch(LodMeshData reference, LodMeshData actual)
    {
        if (!BytesEqual(reference.Vertices, actual.Vertices)) return "Vertices";
        if (!BytesEqual(reference.Indices, actual.Indices)) return "Indices";
        if (!BytesEqual(reference.WaterVertices, actual.WaterVertices)) return "WaterVertices";
        if (!BytesEqual(reference.WaterIndices, actual.WaterIndices)) return "WaterIndices";
        if (!BytesEqual(reference.ColumnCyMin, actual.ColumnCyMin)) return "ColumnCyMin";
        if (!BytesEqual(reference.ColumnCyMax, actual.ColumnCyMax)) return "ColumnCyMax";
        if (reference.YMin != actual.YMin) return "YMin";
        if (reference.YMax != actual.YMax) return "YMax";
        return null;
    }

    /// <summary>
    /// C5's whole premise, pinned: <see cref="LodRegion.BuildMeshData"/> is pure, so which thread
    /// runs it cannot change what it produces. Four threads rebuild the same three level-0 regions
    /// through their own <see cref="MeshBufferPool"/>s and every output must be byte-identical to a
    /// reference taken single-threaded. This is the fact that would fail if a build ever grew hidden
    /// shared mutable state — the two caches it reads (<c>Chunk.ColumnCache</c> and the tree
    /// generator's) are concurrent dictionaries whose entries are deterministic functions of the
    /// world seed, and a wholesale <c>Clear()</c> under a build regenerates identical columns, so a
    /// mismatch here means real state was introduced, not that a cache raced.
    ///
    /// <para>Each thread owns its pool because a shared one is a different fact (fact 4 owns that);
    /// keeping them separate means a mismatch here cannot be a recycled-list bug in disguise.</para>
    /// </summary>
    [Fact]
    public void RegionBuildsAreIdenticalWhicheverThreadRunsThem()
    {
        var coords = new[] { (0, 0), (1, 0), (0, 1) };
        var referencePool = new LodManager(null!).MeshBuffers;
        var references = new LodMeshData[coords.Length];
        for (var i = 0; i < coords.Length; i++)
        {
            references[i] = LodRegion.BuildMeshData(0, coords[i].Item1, coords[i].Item2, referencePool);
            // Deliberately NOT returned: the references are the oracle for the whole run and must
            // not be recycled out from under it.
            Assert.NotEmpty(references[i].Vertices);
        }

        var failures = new ConcurrentBag<string>();
        var builds = 0;
        const int deadlineMs = 1_500;
        var clock = Stopwatch.StartNew();
        var threads = new Thread[4];

        for (var t = 0; t < threads.Length; t++)
        {
            var seed = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    var pool = new LodManager(null!).MeshBuffers;
                    var next = seed;
                    while (clock.ElapsedMilliseconds < deadlineMs)
                    {
                        var i = next++ % coords.Length;
                        var built = LodRegion.BuildMeshData(0, coords[i].Item1, coords[i].Item2, pool);
                        var mismatch = DescribeMismatch(references[i], built);
                        if (mismatch != null)
                            failures.Add($"region {coords[i]} differed in {mismatch}");
                        pool.Return(built.Vertices, built.Indices, built.WaterVertices, built.WaterIndices);
                        Interlocked.Increment(ref builds);
                    }
                }
                catch (Exception ex)
                {
                    failures.Add(ex.ToString());
                }
            })
            {
                IsBackground = true,
                Name = $"LodPurityStress-{seed}"
            };
            threads[t].Start();
        }

        foreach (var thread in threads)
            thread.Join();

        Assert.True(failures.IsEmpty, string.Join(" | ", failures));
        // Anti-vacuity: a deadline that expired before any thread built anything would pass above.
        Assert.True(builds >= 12, $"only {builds} concurrent builds completed; the fact would be vacuous");
    }

    /// <summary>What one stress run observed. Everything is read on the test thread after the
    /// workers have joined.</summary>
    private sealed class StressResult
    {
        public LodManager Manager = null!;
        public List<(int Level, int X, int Z)> Drained = new();
        public List<LodMeshData> Outstanding = new();
        public ConcurrentBag<string> Failures = new();
        public int MaxPendingObserved;
        public int MaxBatchObserved;
        public long ElapsedMs;
    }

    /// <summary>
    /// Runs <see cref="StressWorkers"/> worker threads against one <see cref="LodManager"/> while the
    /// test thread plays the main thread's drain, and reports what was observed. The drain records
    /// every completed build's key and, unless <paramref name="keepBuffers"/> is set, hands the four
    /// lists straight back to <see cref="LodManager.MeshBuffers"/> as
    /// <c>ApplyCompletedMeshes</c> does — no GL is touched, because nothing is ever turned into a
    /// <see cref="LodRegion"/>.
    ///
    /// <para>Regions are never added to <c>Regions</c>, so the closest-needed scan keeps handing out
    /// fresh keys until every offset of every level has been claimed once — 591 in total. That is
    /// what makes "exactly once" checkable here: the claim, not an upload, is what retires a key.</para>
    /// </summary>
    private static StressResult RunStress(int targetBuilds, int drainSleepMs, int deadlineMs,
        bool keepBuffers = false)
    {
        var result = new StressResult { Manager = ClaimableManager() };
        var lods = result.Manager;
        var running = true;
        var clock = Stopwatch.StartNew();
        var threads = new Thread[StressWorkers];

        for (var t = 0; t < threads.Length; t++)
        {
            var id = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    while (Volatile.Read(ref running) && clock.ElapsedMilliseconds < deadlineMs)
                    {
                        switch (lods.TryBuildOneRegion())
                        {
                            case LodBuildOutcome.Idle:
                                // Everything is claimed; spin rather than sleeping the production
                                // 200 ms, which would burn the deadline doing nothing.
                                Thread.Yield();
                                break;
                            case LodBuildOutcome.Throttled:
                                Thread.Sleep(WorldGenerationConfig.LodBuildThrottleDelayMs);
                                break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.Failures.Add(ex.ToString());
                }
            })
            {
                IsBackground = true,
                Name = $"LodBuildStress-{id}"
            };
            threads[t].Start();
        }

        // The drain. Sampled BEFORE dequeuing, so the depth recorded is the depth the workers
        // actually produced rather than whatever is left after the consumer has eaten into it.
        while (result.Drained.Count < targetBuilds && clock.ElapsedMilliseconds < deadlineMs)
        {
            var pending = lods.PendingUploadCount;
            if (pending > result.MaxPendingObserved) result.MaxPendingObserved = pending;

            var batch = new List<LodMeshData>();
            while (lods.TryTakeCompletedMesh(out var data))
                batch.Add(data);
            if (batch.Count > result.MaxBatchObserved) result.MaxBatchObserved = batch.Count;

            foreach (var data in batch)
            {
                result.Drained.Add((data.Level, data.X, data.Z));
                if (keepBuffers)
                    result.Outstanding.Add(data);
                else
                    lods.MeshBuffers.Return(data.Vertices, data.Indices, data.WaterVertices, data.WaterIndices);
            }

            if (drainSleepMs > 0) Thread.Sleep(drainSleepMs);
        }

        Volatile.Write(ref running, false);
        foreach (var thread in threads)
            thread.Join();

        // Whatever the workers enqueued after the loop's last pass still has to come back, or the
        // pool facts would be reading a half-drained queue.
        while (lods.TryTakeCompletedMesh(out var data))
        {
            result.Drained.Add((data.Level, data.X, data.Z));
            if (keepBuffers)
                result.Outstanding.Add(data);
            else
                lods.MeshBuffers.Return(data.Vertices, data.Indices, data.WaterVertices, data.WaterIndices);
        }

        result.ElapsedMs = clock.ElapsedMilliseconds;
        return result;
    }

    /// <summary>Valid keys with the camera at the origin: every level's camera region is
    /// <c>floor(0 / RegionBlocks) = (0, 0)</c>, and the level's offset set is the closed disc of
    /// radius <c>RegionRadius</c>. Re-derived here rather than read from <c>LodManager</c>'s
    /// <c>SortedOffsets</c>, which is private — and an independent derivation is the better oracle
    /// anyway.</summary>
    private static bool IsValidOffset((int Level, int X, int Z) key)
    {
        if (key.Level < 0 || key.Level >= LodRegion.Levels.Length) return false;
        var r = LodRegion.Levels[key.Level].RegionRadius;
        return key.X * key.X + key.Z * key.Z <= r * r;
    }

    /// <summary>
    /// The stress fact. Three workers racing on the same claim paths must between them hand out
    /// every region exactly once: a key that comes out twice is two concurrent builds of one region
    /// applying in completion order, which is the stale-LOD-shell artefact the TryAdd-gated claim
    /// exists to prevent. Also checks the invariants that only hold once the workers are done —
    /// no worker exception escaped, every key is a real offset of its level, and
    /// <c>BuildingClaimCount</c> is back to zero, i.e. every claim taken was released.
    ///
    /// <para><b>Be precise about which line this fact bites on.</b> Nothing here edits the world, so
    /// <c>_dirtyRegions</c> stays empty and every claim goes through
    /// <see cref="LodManager.FindClosestNeededRegion"/>, where <c>_queued.TryAdd</c> would already
    /// exclude duplicates on its own. What the exactness check pins under load is therefore that the
    /// <c>_building</c> registration and its mandatory <c>_queued</c> rollback did not BREAK that
    /// exactness — a rollback that fired on the winning path, or a leaked claim, hands the same key
    /// out twice and fails here, which was confirmed by mutating the rollback to run unconditionally
    /// (this fact failed; the other three passed). The dirty-path collision itself — two workers
    /// racing for a key <see cref="LodManager.MarkEditedColumn"/> re-dirtied mid-build — is not
    /// reachable from here and is pinned deterministically by
    /// <c>LodManagerTests.TwoClaimsNeverYieldTheSameKeyWhileTheFirstIsHeld</c> instead.</para>
    /// </summary>
    [Fact]
    public void ThreeWorkersBetweenThemClaimEveryRegionExactlyOnce()
    {
        var result = RunStress(targetBuilds: 60, drainSleepMs: 0, deadlineMs: 30_000);

        Assert.True(result.Failures.IsEmpty, string.Join(" | ", result.Failures));
        Assert.True(result.Drained.Count >= 40,
            $"only {result.Drained.Count} builds completed in {result.ElapsedMs} ms; the fact would be vacuous");
        Assert.Equal(result.Drained.Count, new HashSet<(int, int, int)>(result.Drained).Count);

        foreach (var key in result.Drained)
            Assert.True(IsValidOffset(key), $"{key} is not an offset of level {key.Level}");

        Assert.Equal(0, result.Manager.BuildingClaimCount);
        Assert.Equal(0, result.Manager.PendingUploadCount);
    }

    /// <summary>
    /// The in-flight cap holds with three producers against a deliberately slow consumer. The bound
    /// is <c>MaxLodBuildsInFlight + StressWorkers</c> (the production bound is
    /// <c>MaxLodBuildsInFlight + MaxConcurrentLodGenerations</c>, which is never larger), not
    /// <c>MaxLodBuildsInFlight</c>: the cap is checked before a claim is taken, so up to
    /// <see cref="StressWorkers"/> workers can already be mid-build when the queue reaches it and each
    /// still enqueues its result. That overshoot is the design, and the bound is what keeps the
    /// retention ceiling derivation in <c>LodManager.MeshBuffers</c> honest — exceed it and the pool
    /// starts dropping returns to the LOH.
    /// </summary>
    [Fact]
    public void TheInFlightCapHoldsUnderThreeProducers()
    {
        // Warm the process (JIT, worldgen caches) exactly as ThreeWorkersBetweenThemClaimEveryRegionExactlyOnce did when
        // xUnit happened to run it first; the order follows a hash of the namespace-qualified name, so it is not stable.
        RunStress(targetBuilds: 60, drainSleepMs: 0, deadlineMs: 30_000);
        var bound = WorldGenerationConfig.MaxLodBuildsInFlight + StressWorkers;
        var result = RunStress(targetBuilds: 60, drainSleepMs: 20, deadlineMs: 30_000);

        Assert.True(result.Failures.IsEmpty, string.Join(" | ", result.Failures));
        Assert.True(result.Drained.Count >= 40,
            $"only {result.Drained.Count} builds completed in {result.ElapsedMs} ms; the fact would be vacuous");
        // Anti-vacuity: a queue that never filled would satisfy the bound trivially. The slow
        // consumer exists to push it up against the cap, so it must actually get there.
        Assert.True(result.MaxPendingObserved >= WorldGenerationConfig.MaxLodBuildsInFlight,
            $"the queue only ever reached {result.MaxPendingObserved}; the cap was never approached");
        Assert.True(result.MaxPendingObserved <= bound,
            $"the completed queue reached {result.MaxPendingObserved}, past the {bound} bound");
    }

    /// <summary>
    /// The rent/return contract under N producers. Two things are checked.
    ///
    /// <para>First, no list is ever handed out to two builds at once. Note what can and cannot be
    /// asserted here: the pool RECYCLES, so the same <c>List&lt;Vertex&gt;</c> instance legitimately
    /// comes back many times over a run and a global "every list was distinct" set would be false by
    /// construction. The invariant that actually matters is SIMULTANEOUS distinctness — two
    /// completed builds sitting on the queue at the same moment must not share a list — so the drain
    /// keeps its buffers instead of returning them, and every list across every build of the whole
    /// run must be distinct. Nothing is recycled during the run, which is what makes the strong form
    /// checkable; the anti-vacuity guard is that the queue genuinely held more than one build at a
    /// time.</para>
    ///
    /// <para>Second, once everything is handed back the pool sits inside its retention ceiling of
    /// 16 with at least one list in it. A count above 16 would mean <c>MeshBufferPool</c>'s
    /// <c>_maxPooled</c> check is not holding under concurrent returns; a count of 0 would mean
    /// returns are being dropped, which is the C8 win inverted.</para>
    /// </summary>
    [Fact]
    public void ConcurrentProducersAndOneConsumerKeepThePoolBounded()
    {
        // A 25 ms drain interval against three builders is several builds per pass, so the queue
        // reliably holds more than one at a time — which is the window the distinctness check needs.
        var result = RunStress(targetBuilds: 40, drainSleepMs: 25, deadlineMs: 30_000, keepBuffers: true);

        Assert.True(result.Failures.IsEmpty, string.Join(" | ", result.Failures));
        Assert.True(result.Outstanding.Count >= 24,
            $"only {result.Outstanding.Count} builds completed in {result.ElapsedMs} ms; the fact would be vacuous");
        Assert.True(result.MaxBatchObserved >= 2,
            "the queue never held two builds at once, so simultaneous distinctness was never exercised");

        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var data in result.Outstanding)
        {
            Assert.True(seen.Add(data.Vertices), "a vertex list was handed to two builds at once");
            Assert.True(seen.Add(data.Indices), "an index list was handed to two builds at once");
            Assert.True(seen.Add(data.WaterVertices), "a water vertex list was handed to two builds at once");
            Assert.True(seen.Add(data.WaterIndices), "a water index list was handed to two builds at once");
        }

        foreach (var data in result.Outstanding)
            result.Manager.MeshBuffers.Return(data.Vertices, data.Indices, data.WaterVertices, data.WaterIndices);

        var pooled = result.Manager.MeshBuffers.PooledVertexLists;
        var pooledTranslucent = result.Manager.MeshBuffers.PooledTranslucentVertexLists;
        Assert.InRange(pooled, 1, 16);
        Assert.InRange(pooledTranslucent, 1, 16);
    }
}
