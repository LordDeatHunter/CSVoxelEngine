using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using VoxelEngine.Player;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.Chunks;

/// <summary>
/// The async mesh path end to end: until now only the synchronous ImmediateRemesh path had
/// coverage, so the dispatcher PERF_TODO C4 replaced the per-chunk Task.Run with had none.
///
/// What is deliberately not tested, and why: peak concurrency cannot be observed here.
/// Headless chunks must stay empty - Chunk.UpdateMesh throws "non-empty chunk mesh without a
/// geometry pool" when a non-empty mesh is applied with a null pool - and an empty chunk
/// returns from GenerateMeshData at the _solidCount == 0 fast path in well under a
/// microsecond, so runners are essentially never in flight together and any sampled maximum
/// reads 0. The cap is guaranteed structurally instead: TryStartMeshRunner claims the count by
/// CAS BEFORE queuing, and each runner decrements exactly once in a finally. What these tests
/// do pin down is that the dispatcher drains every queued build, balances its counter back to
/// zero across repeated rounds, and that scheduling no longer allocates per chunk.
/// </summary>
public sealed class MeshSchedulingTests
{
    private readonly ITestOutputHelper _output;

    public MeshSchedulingTests(ITestOutputHelper output)
    {
        _output = output;
        TestContent.EnsureLoaded();
    }

    // A 6x6x6 block of empty, ready chunks (positions -2..3 on each axis) so the inner 4x4x4
    // (positions -1..2, exactly 64 chunks) have all six face neighbors and are therefore the
    // ones ProcessDirtyChunks schedules. 64 == MaxDirtyChunksPerFrame, so one Update schedules
    // all of them through the un-sorted branch; MaxMeshUploadsPerFrame is 32, so applying them
    // takes two more Update calls.
    private static (ChunkManager Manager, List<Vector3> Meshable) BuildBlock()
    {
        var manager = new ChunkManager(null!);
        var meshable = new List<Vector3>();
        for (var x = -2; x <= 3; x++)
        for (var y = -2; y <= 3; y++)
        for (var z = -2; z <= 3; z++)
        {
            var pos = new Vector3(x, y, z);
            var chunk = new Chunk(null!, pos, generateBlockData: false);
            chunk.IsBlockDataReady = true;
            manager.Chunks[pos] = chunk;
            if (x >= -1 && x <= 2 && y >= -1 && y <= 2 && z >= -1 && z <= 2) meshable.Add(pos);
        }
        return (manager, meshable);
    }

    private static Camera AtOrigin()
    {
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(8f, 8f, 8f), yaw: 0f, pitch: 0f);
        return camera;
    }

    // Spins on the TEST thread (never blocking a pool thread); returns false on timeout so a
    // stranded work item or a leaked runner count fails loudly instead of hanging the suite.
    private static bool WaitUntil(Func<bool> done, int timeoutMs = 15000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (done()) return true;
            Thread.Sleep(1);
        }
        return done();
    }

    private static bool Quiescent(ChunkManager m) =>
        m.GeneratingMeshCount == 0 && m.PendingMeshWorkCount == 0 && m.ActiveMeshRunnerCount == 0;

    // Schedules one full round and applies every result. Returns once all 64 are meshed.
    private static void RunRound(ChunkManager manager, List<Vector3> meshable, Camera camera)
    {
        foreach (var pos in meshable) manager.MarkDirty(pos);
        manager.Update(camera);                       // schedules
        Assert.True(WaitUntil(() => Quiescent(manager)), "mesh dispatcher did not drain");
        manager.Update(camera);                       // applies up to MaxMeshUploadsPerFrame
        manager.Update(camera);                       // applies the rest
    }

    [Fact]
    public void EveryScheduledChunkMeshesAndTheQueuesDrain()
    {
        var (manager, meshable) = BuildBlock();
        // A fixture typo must not silently shrink the workload below the per-frame budget.
        Assert.Equal(StreamingConfig.MaxDirtyChunksPerFrame, meshable.Count);

        RunRound(manager, meshable, AtOrigin());

        foreach (var pos in meshable)
            Assert.True(manager.Chunks[pos].IsMeshed, $"chunk {pos} never meshed");
        Assert.Equal(0, manager.DirtyCount);
        Assert.True(Quiescent(manager));
    }

    [Fact]
    public void RepeatedRoundsNeverStrandWork()
    {
        var (manager, meshable) = BuildBlock();
        var camera = AtOrigin();

        // Rounds 2+ are the real regression guard: if _activeMeshRunners ever leaks a count (a
        // skipped finally, or the drain/restart handshake done in the wrong order) the cap
        // fills permanently and a later round times out on the drain.
        for (var round = 0; round < 5; round++)
        {
            RunRound(manager, meshable, camera);
            foreach (var pos in meshable)
                Assert.True(manager.Chunks[pos].IsMeshed, $"chunk {pos} unmeshed in round {round}");
            Assert.True(Quiescent(manager), $"dispatcher not quiescent after round {round}");
        }
    }

    [Fact]
    public void SchedulingAMeshDoesNotAllocatePerChunk()
    {
        var (manager, meshable) = BuildBlock();
        var camera = AtOrigin();

        // Warm-up round: JITs the dispatch path and grows _generatingMeshes' table and the
        // pending queue's segment, so the measured frame pays only steady-state costs.
        RunRound(manager, meshable, camera);

        foreach (var pos in meshable) manager.MarkDirty(pos);   // marking is not the measured part

        // Per-thread counter, so this measures only the SCHEDULING thread's allocation - which
        // is exactly the per-frame cost C4 targets. The build itself runs on pool threads and
        // is unchanged by C4.
        var before = GC.GetAllocatedBytesForCurrentThread();
        manager.Update(camera);                                  // schedules all 64
        var perChunk = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)meshable.Count;

        // Task.Run(async closure) cost ~400 B per chunk on the scheduling thread (display class +
        // delegate + Task + unwrap promise). The work-item dispatcher leaves only the unchanged
        // _generatingMeshes node plus an amortized queue slot, ~80 B. If this is close to the
        // budget, print it and find out why rather than raising the budget.
        Assert.True(perChunk < 256, $"scheduling allocated {perChunk:F0} B per chunk");

        // The _generatingMeshes dedupe gate (the TryAdd at the top of StartMeshGeneration plus
        // the ContainsKey skip in ProcessDirtyChunks) is unchanged code and cannot be asserted
        // through the public surface without racing the build, so it is not checked here.
        Assert.True(WaitUntil(() => Quiescent(manager)), "mesh dispatcher did not drain");
        manager.Update(camera);
        manager.Update(camera);
    }

    /// <summary>
    /// <see cref="ChunkManager.DrainMeshWork"/> returns only once the build queue is empty and
    /// every pool runner has exited, so a world-seed swap after it cannot race a mesh build, and
    /// the finished result is still there for the next Update to apply. The whole walled area
    /// is stone, so the center is a real 4096-cell build (not the empty-chunk fast path) whose
    /// faces are all culled: its mesh is empty and applies headlessly.
    /// </summary>
    [Fact]
    public void DrainMeshWorkWaitsForEveryRunner()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        MeshTestWorld.FillWalledArea(manager, ContentRegistry.RequireBlock("stone").Id);
        var camera = AtOrigin();

        manager.MarkDirty(center.Position);
        manager.Update(camera);                       // schedules the build

        Assert.True(manager.DrainMeshWork(TimeSpan.FromSeconds(5)), "mesh work did not drain");
        Assert.Equal(0, manager.PendingMeshWorkCount);
        Assert.Equal(0, manager.ActiveMeshRunnerCount);

        manager.Update(camera);                       // applies the drained result
        Assert.True(center.IsMeshed, "the drained build was never applied");
    }

    [Fact]
    public void CompletedPreEditMeshDoesNotOverwriteSynchronousEdit()
    {
        var centerPosition = MeshTestWorld.HighAbove;
        var (manager, center) = MeshTestWorld.WalledArea(centerPosition);
        manager.Headless = true;
        center.SetBlockRaw(8, 8, 8, ContentRegistry.RequireBlock("stone").Id, 0);

        var scheduler = manager.MeshSchedulerForTests;
        var beforeApplyVertexCounts = new List<int>();
        var asyncBuilds = 0;
        var generatedOldVertexCount = 0;
        scheduler.MeshBeforeApplyForTests = (chunk, meshData) =>
        {
            if (ReferenceEquals(chunk, center))
                beforeApplyVertexCounts.Add(meshData.Vertices.Count);
        };
        scheduler.BuildAcceptedForTests = chunk =>
        {
            if (ReferenceEquals(chunk, center)) Interlocked.Increment(ref asyncBuilds);
        };
        scheduler.MeshGeneratedForTests = (chunk, meshData) =>
        {
            if (ReferenceEquals(chunk, center) && Volatile.Read(ref asyncBuilds) == 1)
                generatedOldVertexCount = meshData.Vertices.Count;
        };

        manager.MarkDirty(centerPosition);
        scheduler.ProcessDirtyChunks([centerPosition]);
        Assert.True(manager.DrainMeshWork(TimeSpan.FromSeconds(10)), "old mesh build did not drain");

        // Drain waits through publication and claim release but intentionally leaves completed
        // outputs queued. No Update/consumer pass has run between the old snapshot and the edit.
        Assert.Equal(0, manager.GeneratingMeshCount);
        Assert.Equal(1, scheduler.CompletedMeshCount);
        Assert.True(generatedOldVertexCount > 0, "old snapshot did not contain the placed stone");

        var editedCell = centerPosition * Chunk.Size + new Vector3(8);
        Assert.True(center.RemoveBlockAt(editedCell, manager));
        Assert.Single(beforeApplyVertexCounts);
        Assert.Equal(0, beforeApplyVertexCounts[0]); // The synchronous result is the empty edited mesh.
        Assert.Equal(1, scheduler.CompletedMeshCount);

        scheduler.ApplyCompletedMeshes([centerPosition]);
        scheduler.ProcessDirtyChunks([centerPosition]);
        Assert.True(manager.DrainMeshWork(TimeSpan.FromSeconds(10)), "post-edit retry did not drain");
        scheduler.ApplyCompletedMeshes([centerPosition]);

        // Reach quiescence: the old result must be rejected or followed by a current rebuild.
        // On the frozen base no retry remains, so the final result is the old one-block mesh.
        _output.WriteLine("completed-before-edit: generatedOldVertices={0}; asyncBuilds={1}; preApplyVertexCounts=[{2}]; completed={3}; generating={4}; pending={5}; runners={6}",
            generatedOldVertexCount, Volatile.Read(ref asyncBuilds), string.Join(",", beforeApplyVertexCounts),
            scheduler.CompletedMeshCount, manager.GeneratingMeshCount, manager.PendingMeshWorkCount,
            manager.ActiveMeshRunnerCount);
        Assert.NotEmpty(beforeApplyVertexCounts);
        Assert.Equal(0, scheduler.CompletedMeshCount);
        Assert.Equal(0, manager.GeneratingMeshCount);
        Assert.Equal(0, manager.PendingMeshWorkCount);
        Assert.Equal(0, manager.ActiveMeshRunnerCount);
        Assert.Equal(0, beforeApplyVertexCounts[^1]);
    }

    [Fact]
    public void EditDuringInFlightBuildPreservesEditedMesh()
    {
        var centerPosition = MeshTestWorld.HighAbove;
        var (manager, center) = MeshTestWorld.WalledArea(centerPosition);
        manager.Headless = true;
        center.SetBlockRaw(8, 8, 8, ContentRegistry.RequireBlock("stone").Id, 0);

        var scheduler = manager.MeshSchedulerForTests;
        var beforeApplyVertexCounts = new List<int>();
        var asyncBuilds = 0;
        var generatedVertexCount = 0;
        var hookTimedOut = 0;
        using var generated = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        scheduler.MeshBeforeApplyForTests = (chunk, meshData) =>
        {
            if (ReferenceEquals(chunk, center))
                beforeApplyVertexCounts.Add(meshData.Vertices.Count);
        };
        scheduler.BuildAcceptedForTests = chunk =>
        {
            if (ReferenceEquals(chunk, center)) Interlocked.Increment(ref asyncBuilds);
        };
        scheduler.MeshGeneratedForTests = (chunk, meshData) =>
        {
            if (!ReferenceEquals(chunk, center) || Volatile.Read(ref asyncBuilds) != 1) return;
            generatedVertexCount = meshData.Vertices.Count;
            generated.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
            {
                Interlocked.Exchange(ref hookTimedOut, 1);
                throw new TimeoutException("mesh test release barrier was not opened");
            }
        };

        try
        {
            manager.MarkDirty(centerPosition);
            scheduler.ProcessDirtyChunks([centerPosition]);
            Assert.True(generated.Wait(TimeSpan.FromSeconds(10)), "worker did not finish old snapshot");
            Assert.True(generatedVertexCount > 0, "old snapshot did not contain the placed stone");

            var editedCell = centerPosition * Chunk.Size + new Vector3(8);
            Assert.True(center.RemoveBlockAt(editedCell, manager));
            Assert.Equal(0, beforeApplyVertexCounts[^1]);

            release.Set();
            Assert.True(manager.DrainMeshWork(TimeSpan.FromSeconds(10)), "old build did not drain");
            Assert.Equal(0, Volatile.Read(ref hookTimedOut));
            scheduler.ApplyCompletedMeshes([centerPosition]);

            scheduler.ProcessDirtyChunks([centerPosition]);
            Assert.True(manager.DrainMeshWork(TimeSpan.FromSeconds(10)), "retry build did not drain");
            scheduler.ApplyCompletedMeshes([centerPosition]);

            Assert.Equal(0, beforeApplyVertexCounts[^1]);
            Assert.Equal(0, scheduler.CompletedMeshCount);
            Assert.Equal(0, manager.GeneratingMeshCount);
            Assert.Equal(0, manager.PendingMeshWorkCount);
            Assert.Equal(0, manager.ActiveMeshRunnerCount);
            _output.WriteLine("in-flight edit: generatedOldVertices={0}; asyncBuilds={1}; preApplyVertexCounts=[{2}]",
                generatedVertexCount, Volatile.Read(ref asyncBuilds), string.Join(",", beforeApplyVertexCounts));
        }
        finally
        {
            release.Set();
            Assert.True(manager.DrainMeshWork(TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public void EditWithNoOldOutputPreservesSynchronousMesh()
    {
        var centerPosition = MeshTestWorld.HighAbove;
        var (manager, center) = MeshTestWorld.WalledArea(centerPosition);
        manager.Headless = true;
        center.SetBlockRaw(8, 8, 8, ContentRegistry.RequireBlock("stone").Id, 0);
        var scheduler = manager.MeshSchedulerForTests;
        var asyncBuilds = 0;
        var beforeApplyVertexCounts = new List<int>();
        scheduler.MeshBeforeApplyForTests = (chunk, meshData) =>
        {
            if (ReferenceEquals(chunk, center))
                beforeApplyVertexCounts.Add(meshData.Vertices.Count);
        };
        scheduler.BuildAcceptedForTests = chunk =>
        {
            if (ReferenceEquals(chunk, center)) Interlocked.Increment(ref asyncBuilds);
        };

        var editedCell = centerPosition * Chunk.Size + new Vector3(8);
        Assert.True(center.RemoveBlockAt(editedCell, manager));
        scheduler.ProcessDirtyChunks([centerPosition]);
        Assert.True(manager.DrainMeshWork(TimeSpan.FromSeconds(10)));
        scheduler.ApplyCompletedMeshes([centerPosition]);

        Assert.NotEmpty(beforeApplyVertexCounts);
        Assert.Equal(0, beforeApplyVertexCounts[^1]);
        Assert.Equal(0, scheduler.CompletedMeshCount);
        Assert.Equal(0, manager.GeneratingMeshCount);
        _output.WriteLine("idle edit: asyncBuilds={0}; preApplyVertexCounts=[{1}]",
            Volatile.Read(ref asyncBuilds), string.Join(",", beforeApplyVertexCounts));
    }

    [Fact]
    public void OldBuildCleanupDoesNotReleaseReplacementClaim()
    {
        var (manager, _) = BuildBlock();
        var camera = AtOrigin();
        var pos = Vector3.Zero;
        var oldChunk = manager.Chunks[pos];
        var replacement = new Chunk(null!, pos, generateBlockData: false) { IsBlockDataReady = true };
        using var oldEntered = new ManualResetEventSlim();
        using var releaseOld = new ManualResetEventSlim();
        using var oldFinished = new ManualResetEventSlim();
        using var replacementEntered = new ManualResetEventSlim();
        using var releaseReplacement = new ManualResetEventSlim();
        var replacementBuilds = 0;
        var scheduler = manager.MeshSchedulerForTests;
        scheduler.BuildAcceptedForTests = chunk =>
        {
            if (ReferenceEquals(chunk, oldChunk)) { oldEntered.Set(); releaseOld.Wait(TimeSpan.FromSeconds(10)); }
            if (ReferenceEquals(chunk, replacement))
            {
                Interlocked.Increment(ref replacementBuilds);
                replacementEntered.Set();
                releaseReplacement.Wait(TimeSpan.FromSeconds(10));
            }
        };
        scheduler.BuildFinishedForTests = chunk => { if (ReferenceEquals(chunk, oldChunk)) oldFinished.Set(); };

        try
        {
            manager.MarkDirty(pos);
            manager.Update(camera);
            Assert.True(oldEntered.Wait(TimeSpan.FromSeconds(5)));

            Assert.True(manager.Chunks.TryRemove(pos, out _));
            scheduler.ForgetChunk(ChunkKey.Pack(pos));
            manager.Chunks[pos] = replacement;
            manager.MarkDirty(pos);
            manager.Update(camera);
            Assert.True(replacementEntered.Wait(TimeSpan.FromSeconds(5)));

            releaseOld.Set();
            Assert.True(oldFinished.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, manager.GeneratingMeshCount);

            manager.MarkDirty(pos);
            manager.Update(camera);
            Assert.Equal(1, manager.GeneratingMeshCount);
            Assert.Equal(0, manager.PendingMeshWorkCount);
            Assert.Equal(1, Volatile.Read(ref replacementBuilds));

            releaseReplacement.Set();
            Assert.True(manager.DrainMeshWork(TimeSpan.FromSeconds(10)));
            manager.Update(camera);
            Assert.True(WaitUntil(() => Volatile.Read(ref replacementBuilds) == 2));
        }
        finally
        {
            releaseOld.Set();
            releaseReplacement.Set();
            Assert.True(manager.DrainMeshWork(TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public void OldQueuedChunkIsNotBuiltAfterReplacement()
    {
        var (manager, _) = BuildBlock();
        var camera = AtOrigin();
        var pos = Vector3.Zero;
        var oldChunk = manager.Chunks[pos];
        var replacement = new Chunk(null!, pos, generateBlockData: false) { IsBlockDataReady = true };
        var blockers = new HashSet<Chunk>
        {
            manager.Chunks[new Vector3(1, 0, 0)],
            manager.Chunks[new Vector3(0, 1, 0)],
            manager.Chunks[new Vector3(0, 0, 1)],
            manager.Chunks[new Vector3(-1, 0, 0)]
        };
        using var release = new ManualResetEventSlim();
        var blocked = 0;
        var accepted = 0;
        var scheduler = manager.MeshSchedulerForTests;
        scheduler.BeforeBuildForTests = chunk =>
        {
            if (blockers.Contains(chunk))
            {
                Interlocked.Increment(ref blocked);
                release.Wait(TimeSpan.FromSeconds(10));
            }
        };
        scheduler.BuildAcceptedForTests = chunk => { if (ReferenceEquals(chunk, oldChunk)) Interlocked.Increment(ref accepted); };

        try
        {
            foreach (var chunk in blockers) manager.MarkDirty(chunk.Position);
            manager.Update(camera);
            Assert.True(WaitUntil(() => Volatile.Read(ref blocked) == StreamingConfig.MaxConcurrentMeshGenerations));

            manager.MarkDirty(pos);
            manager.Update(camera);
            Assert.Equal(1, manager.PendingMeshWorkCount);
            Assert.True(manager.Chunks.TryRemove(pos, out _));
            scheduler.ForgetChunk(ChunkKey.Pack(pos));
            manager.Chunks[pos] = replacement;
            release.Set();
            Assert.True(manager.DrainMeshWork(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, Volatile.Read(ref accepted));
        }
        finally
        {
            release.Set();
            Assert.True(manager.DrainMeshWork(TimeSpan.FromSeconds(10)));
        }
    }
}
