using System.Collections.Generic;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// End-to-end cover for the chunk-generation scan after PERF_TODO C3 replaced its per-call full
/// walk of <see cref="ChunkGenerationWorker.SortedOffsets"/> with a resume from the shared
/// <c>ChunkScanFrontier</c>. These facts run against the real <see cref="ChunkManager.NeedsChunk"/>
/// and a real <see cref="Camera"/>; the worker is constructed but <see cref="ChunkGenerationWorker.Start"/>
/// is never called, so no thread exists and the facts drive <c>FindClosestNeededChunk</c> directly.
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
    /// round, including across camera moves. Equivalence is asserted on the CHOICE, which is the
    /// property C3 had to preserve - not on how the scan reached it.
    /// </summary>
    [Fact]
    public void WorkerPicksTheSameChunkTheFullScanWould()
    {
        var manager = new ChunkManager(null!);
        var camera = new Camera(null, null);
        var worker = new ChunkGenerationWorker(manager, camera);

        // Chunk-aligned and away from the origin, so a sign or a Floor() mistake cannot hide.
        var poses = new[]
        {
            new Vector3(80f, 96f, -64f),
            new Vector3(80f, 96f, -48f),
            new Vector3(-160f, 32f, 208f)
        };
        camera.TestTeleport(poses[0], 0f, 0f);

        var nonNull = 0;
        for (var round = 0; round < 400; round++)
        {
            // Two moves during the run: the frontier must reset to the closest offset for the new
            // centre rather than resuming someone else's prefix.
            if (round == 150) camera.TestTeleport(poses[1], 0f, 0f);
            if (round == 300) camera.TestTeleport(poses[2], 0f, 0f);

            var camChunk = (camera.Position / Chunk.Size).Floor();
            var expected = ReferenceScan(manager, camChunk);
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
    /// Pins the property the frontier's closest-first claim rests on: the offset array is ordered by
    /// non-decreasing distance and is exactly the set of integer offsets inside the radius.
    /// Tie order among equidistant offsets is deliberately NOT pinned - <c>BuildSortedOffsets</c>
    /// uses an unstable <c>List.Sort</c>, so that order is arbitrary (though fixed for the process,
    /// since the array is built once in a static initialiser).
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
}
