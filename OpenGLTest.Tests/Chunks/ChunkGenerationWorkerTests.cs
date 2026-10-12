using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using Xunit;

namespace VoxelEngine.Tests.Chunks;

/// <summary>
/// End-to-end cover for the chunk-generation scan after PERF_TODO C3 replaced its per-call full
/// walk of <see cref="ChunkGenerationWorker.SortedOffsets"/> with a resume from the shared
/// <c>ChunkScanFrontier</c>s (one per streaming focus). These facts run against the real
/// <see cref="ChunkManager.NeedsChunk"/> and a real <see cref="StreamingFocus"/>; the worker is
/// constructed but <see cref="ChunkGenerationWorker.Start"/> is never called (bar the stop fact), so
/// no thread exists and the facts drive <c>FindClosestNeededChunk</c> directly.
///
/// <para><c>ChunkManager.QueueChunk</c> is deliberately NOT driven: it runs real worldgen
/// synchronously, which would turn a 400-round equivalence sweep into a several-minute terrain
/// build for no extra coverage. The claim it makes is simulated instead by inserting a
/// <c>new Chunk(null!, pos, generateBlockData: false)</c> straight into <see cref="ChunkManager.Chunks"/>,
/// which is exactly and only what <see cref="ChunkManager.NeedsChunk"/> reads (it is
/// <c>!Chunks.ContainsKey(pos) &amp;&amp; !_queuedChunkPositions.ContainsKey(pos)</c>).</para>
///
/// <para>What is covered here is the CHOICE the worker makes - that the frontier hands back the same
/// position the pre-C3 full scan would have - plus the closest-first ordering property that choice
/// rests on. The threading argument itself lives on <c>ChunkScanFrontier</c> and is checked by
/// <c>ChunkScanFrontierTests</c>, not here.</para>
/// </summary>
public sealed class ChunkGenerationWorkerTests
{
    public ChunkGenerationWorkerTests() => TestContent.EnsureLoaded();

    /// <summary>
    /// Re-expresses the pre-C3 scan verbatim (the loop that used to live in
    /// <c>ChunkGenerationWorker.FindClosestNeededChunk</c>) as the oracle.
    /// </summary>
    private static Vector3? ReferenceScan(ChunkManager manager, Vector3 camChunk)
    {
        foreach (var offset in ChunkGenerationWorker.SortedOffsets)
        {
            var pos = camChunk + offset;
            if (manager.NeedsChunk(pos))
                return pos;
        }

        return null;
    }

    /// <summary>
    /// The frontier-backed scan picks the same chunk the old full scan would have, round after
    /// round, including across focus moves. Equivalence is asserted on the CHOICE, which is the
    /// property C3 had to preserve - not on how the scan reached it.
    /// </summary>
    [Fact]
    public void WorkerPicksTheSameChunkTheFullScanWould()
    {
        var manager = new ChunkManager(null!);
        var focus = new StreamingFocus();
        var worker = new ChunkGenerationWorker(manager, focus);

        // Chunk-aligned and away from the origin, so a sign or a Floor() mistake cannot hide.
        var poses = new[]
        {
            new Vector3(80f, 96f, -64f),
            new Vector3(80f, 96f, -48f),
            new Vector3(-160f, 32f, 208f)
        };
        focus.SetEye(poses[0]);

        var nonNull = 0;
        for (var round = 0; round < 400; round++)
        {
            // Two moves during the run: the frontier must reset to the closest offset for the new
            // centre rather than resuming someone else's prefix.
            if (round == 150) focus.SetEye(poses[1]);
            if (round == 300) focus.SetEye(poses[2]);

            var expected = ReferenceScan(manager, focus.Chunks[0]);
            var actual = worker.FindClosestNeededChunk();

            Assert.Equal(expected, actual);

            if (!actual.HasValue) continue;

            nonNull++;
            manager.Chunks[actual.Value] = new Chunk(null!, actual.Value, generateBlockData: false);
        }

        // Guard: a scan that always returned null would satisfy every assertion above.
        Assert.Equal(400, nonNull);
    }

    /// <summary>
    /// Two foci whose load spheres do not touch are generated around together: every claim is the
    /// chunk the full scan would pick for one of the two foci (so it lies inside that focus's
    /// radius), and both foci receive claims long before either sphere is complete, so one player's
    /// streaming never waits for another's to finish.
    /// </summary>
    [Fact]
    public void TwoFociAreBothServed()
    {
        var manager = new ChunkManager(null!);
        var focus = new StreamingFocus();
        var worker = new ChunkGenerationWorker(manager, focus);

        // Further apart than two load radii, so no chunk is inside both spheres and each claim
        // belongs to exactly one focus.
        const int apartChunks = 40;
        Assert.True(apartChunks > 2 * WorldGenerationConfig.ChunkRadius);
        var near = new Vector3(80f, 96f, -64f);
        var far = near + new Vector3(apartChunks * Chunk.Size, 0f, 0f);
        focus.SetEyes([near, far]);
        var foci = focus.Chunks;
        Assert.Equal(2, foci.Length);

        var claims = new int[foci.Length];
        for (var round = 0; round < 400; round++)
        {
            var expectedNear = ReferenceScan(manager, foci[0]);
            var expectedFar = ReferenceScan(manager, foci[1]);
            var actual = worker.FindClosestNeededChunk();

            Assert.True(actual.HasValue, $"round {round}: no chunk claimed while both spheres were incomplete");
            var claimed = actual!.Value;
            Assert.True(claimed == expectedNear || claimed == expectedFar,
                $"round {round}: {claimed} is neither focus's closest needed chunk");

            var owner = claimed == expectedNear ? 0 : 1;
            Assert.True(Vector3.DistanceSquared(claimed, foci[owner]) <= WorldGenerationConfig.ChunkRadiusSq,
                $"round {round}: {claimed} lies outside its focus's radius");
            Assert.True(StreamingFocus.WithinRadius(foci, claimed, WorldGenerationConfig.ChunkRadiusSq));
            claims[owner]++;

            manager.Chunks[claimed] = new Chunk(null!, claimed, generateBlockData: false);
        }

        Assert.True(claims[0] > 0, "the first focus never received a claim");
        Assert.True(claims[1] > 0, "the second focus never received a claim");
    }

    /// <summary>
    /// Pins the property the frontier's closest-first claim rests on: the offset array is ordered by
    /// non-decreasing distance and is exactly the set of integer offsets inside the radius.
    /// Tie order among equidistant offsets is deliberately NOT pinned - <c>BuildSortedOffsets</c>
    /// uses an unstable <c>List.Sort</c>, so that order is arbitrary (though fixed for the process,
    /// since the array is built once in a static initializer).
    /// </summary>
    [Fact]
    public void SortedOffsetsAreClosestFirstAndCoverTheRadius()
    {
        var offsets = ChunkGenerationWorker.SortedOffsets;
        Assert.NotEmpty(offsets);

        var previous = -1f;
        foreach (var offset in offsets)
        {
            var lengthSq = offset.LengthSquared();
            Assert.True(lengthSq >= previous, $"offsets are not closest-first at {offset}");
            previous = lengthSq;
        }

        var actual = new HashSet<Vector3>();
        foreach (var offset in offsets)
            Assert.True(actual.Add(offset), $"duplicate offset {offset}");

        var expected = new HashSet<Vector3>();
        const int radius = WorldGenerationConfig.ChunkRadius;
        for (var dx = -radius; dx <= radius; dx++)
        for (var dy = -radius; dy <= radius; dy++)
        for (var dz = -radius; dz <= radius; dz++)
        {
            if (dx * dx + dy * dy + dz * dz <= WorldGenerationConfig.ChunkRadiusSq)
                expected.Add(new Vector3(dx, dy, dz));
        }

        // Set equality both ways: nothing inside the radius is missing, nothing outside it is
        // present. The count assertion also stops the fact passing on two empty sets.
        Assert.True(expected.Count > 1000);
        Assert.Equal(expected.Count, actual.Count);
        Assert.True(actual.SetEquals(expected));
    }

    /// <summary>
    /// <see cref="ChunkGenerationWorker.Stop"/> joins every thread it started, so once it
    /// returns true nothing generates any more (the precondition a world-seed swap needs), and
    /// the worker can be started and stopped again. The focus sits far above the terrain, so
    /// every chunk the pool claims takes the above-terrain early return and the run stays cheap.
    /// </summary>
    [Fact]
    public void StopJoinsEveryWorkerThread()
    {
        var manager = new ChunkManager(null!);
        var focus = new StreamingFocus();
        focus.SetEye(new Vector3(0f, 1e6f, 0f));
        var worker = new ChunkGenerationWorker(manager, focus);

        for (var round = 0; round < 2; round++)
        {
            var loadedBefore = manager.LoadedChunkCount;
            worker.Start();
            Assert.True(WaitUntil(() => manager.LoadedChunkCount > loadedBefore),
                $"round {round}: the pool never generated a chunk");

            Assert.True(worker.Stop(), $"round {round}: a worker outlived its join");
            Assert.False(worker.AnyThreadAlive);

            // Joined means quiet: nothing may still be adding chunks after Stop returned.
            var loadedAfterStop = manager.LoadedChunkCount;
            Thread.Sleep(50);
            Assert.Equal(loadedAfterStop, manager.LoadedChunkCount);
        }
    }

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
}
