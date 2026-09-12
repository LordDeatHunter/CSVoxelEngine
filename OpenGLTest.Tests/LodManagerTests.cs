using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>Covers the item-19 slicing of the buried-flag pass: new regions evaluate the
/// same pass they are added in, still-buried regions re-evaluate every pass (a stale
/// buried flag is a horizon hole), and the unburied level-0 slice converges over
/// successive passes instead of testing everything at once.</summary>
public sealed class LodManagerTests
{
    public LodManagerTests() => TestContent.EnsureLoaded();

    private static LodRegion HeadlessRegion(int x, int z, int cy)
    {
        var span = LodRegion.Levels[0].RegionBlocks / Chunk.Size;
        var n = span * span;
        var mins = new int[n];
        var maxs = new int[n];
        Array.Fill(mins, cy);
        Array.Fill(maxs, cy);
        var data = new LodMeshData(0, x, z, [], [], [], [], 0f, 0f, mins, maxs);
        return new LodRegion(null!, data);
    }

    // A coarse-level (level > 0) region's burial test never touches ColumnCyMin/Max
    // (see IsCoveredByFinerLevel), so empty arrays are safe here.
    private static LodRegion CoarseHeadlessRegion(int level, int x, int z)
    {
        var data = new LodMeshData(level, x, z, [], [], [], [], 0f, 0f, [], []);
        return new LodRegion(null!, data);
    }

    private static void MeshColumns(ChunkManager chunks, int baseCx, int baseCz, int cy)
    {
        var empty = new ChunkMeshData([], [], [], [], false);
        for (var cx = baseCx; cx < baseCx + 4; cx++)
        for (var cz = baseCz; cz < baseCz + 4; cz++)
        {
            var pos = new Vector3(cx, cy, cz);
            var chunk = new Chunk(null!, pos, generateBlockData: false);
            chunk.IsBlockDataReady = true;
            chunk.UpdateMesh(empty, null, null);
            chunks.Chunks[pos] = chunk;
        }
    }

    [Fact]
    public void CoverSliceConvergesWithoutStuckShells()
    {
        var chunks = new ChunkManager(null!);
        MeshColumns(chunks, 0, 0, 7);

        var lods = new LodManager(null!);
        // Inserted first so the slice reaches it last: it must still converge.
        var covered = HeadlessRegion(0, 0, 7);
        lods.AddRegion((0, 0, 0), covered);
        var lateCovered = HeadlessRegion(1, 0, 7);
        lods.AddRegion((0, 1, 0), lateCovered);
        var control = HeadlessRegion(4, 4, 7);
        lods.AddRegion((0, 4, 4), control);
        for (var rx = -4; rx <= 4; rx++)
        for (var rz = -4; rz <= 4; rz++)
        {
            if ((rx == 0 && rz == 0) || (rx == 1 && rz == 0) || (rx == 4 && rz == 4))
                continue;
            lods.AddRegion((0, rx, rz), HeadlessRegion(rx, rz, 7));
        }

        // Pass 1: everything is newly added, so everything evaluates at once.
        lods.UpdateCoverFlags(chunks, Vector3.Zero);
        Assert.True(covered.IsBuried);
        Assert.False(lateCovered.IsBuried);
        Assert.False(control.IsBuried);

        // Cover the late region, then run one more pass: the 64-wide slice cannot
        // reach it at the head of the list, so it must stay unburied this pass...
        MeshColumns(chunks, 4, 0, 7);
        lods.UpdateCoverFlags(chunks, Vector3.Zero);
        Assert.True(covered.IsBuried);
        Assert.False(lateCovered.IsBuried);

        // ...but the unconverged cycle keeps the pass running until it does.
        lods.UpdateCoverFlags(chunks, Vector3.Zero);
        Assert.True(covered.IsBuried);
        Assert.True(lateCovered.IsBuried);
        Assert.False(control.IsBuried);
    }

    /// <summary>Covers item D4: IsCoveredByFinerLevel now reads a loaded-child count kept by
    /// AddRegion/RemoveRegion instead of probing Regions directly, so a coarse region must
    /// flip buried only once every one of its ratio*ratio finer children is loaded, and flip
    /// back the moment any of them unloads.</summary>
    [Fact]
    public void CoarseRegionBuriedOnlyWhenEveryFinerChildIsLoaded()
    {
        var chunks = new ChunkManager(null!);
        var lods = new LodManager(null!);

        // Level 1's RegionBlocks (128) / level 0's (64) = ratio 2, so region (1,0,0) is
        // covered exactly when all four of level 0's (0,0)/(0,1)/(1,0)/(1,1) are loaded.
        var coarse = CoarseHeadlessRegion(1, 0, 0);
        lods.AddRegion((1, 0, 0), coarse);

        lods.AddRegion((0, 0, 0), HeadlessRegion(0, 0, 7));
        lods.AddRegion((0, 0, 1), HeadlessRegion(0, 1, 7));
        lods.AddRegion((0, 1, 0), HeadlessRegion(1, 0, 7));

        lods.UpdateCoverFlags(chunks, Vector3.Zero);
        Assert.False(coarse.IsBuried);

        var lastChild = HeadlessRegion(1, 1, 7);
        lods.AddRegion((0, 1, 1), lastChild);
        lods.UpdateCoverFlags(chunks, Vector3.Zero);
        Assert.True(coarse.IsBuried);

        // Unloading any one child must uncover the coarse region again.
        lods.Regions.TryRemove((0, 1, 1), out var removed);
        lods.RemoveRegion(removed!);
        lods.UpdateCoverFlags(chunks, Vector3.Zero);
        Assert.False(coarse.IsBuried);
    }

    /// <summary>Covers item D4's incremental bookkeeping across a same-key rebuild: LodManager.
    /// Update removes the old region at a key and immediately adds its replacement at that same
    /// key (a mesh rebuild), which must leave the parent's loaded-child count unchanged rather
    /// than drifting up or down every rebuild.</summary>
    [Fact]
    public void RebuildingARegionAtItsOwnKeyLeavesParentCoverageUnchanged()
    {
        var chunks = new ChunkManager(null!);
        var lods = new LodManager(null!);

        var coarse = CoarseHeadlessRegion(1, 0, 0);
        lods.AddRegion((1, 0, 0), coarse);

        lods.AddRegion((0, 0, 0), HeadlessRegion(0, 0, 7));
        lods.AddRegion((0, 0, 1), HeadlessRegion(0, 1, 7));
        lods.AddRegion((0, 1, 0), HeadlessRegion(1, 0, 7));
        var original = HeadlessRegion(1, 1, 7);
        lods.AddRegion((0, 1, 1), original);

        lods.UpdateCoverFlags(chunks, Vector3.Zero);
        Assert.True(coarse.IsBuried);

        // Same remove-then-add-at-the-same-key sequence LodManager.Update runs when a region's
        // mesh is rebuilt (see the `replaced` handling there).
        lods.RemoveRegion(original);
        var rebuilt = HeadlessRegion(1, 1, 7);
        lods.AddRegion((0, 1, 1), rebuilt);

        lods.UpdateCoverFlags(chunks, Vector3.Zero);
        Assert.True(coarse.IsBuried);
    }

    /// <summary>Covers item D4 at negative region coordinates, the case a truncating integer
    /// divide silently gets wrong: C# `/` rounds toward zero, so level-0 child x = -3 would map
    /// onto parent x = -1 instead of -2 and be counted into the wrong coarse region. Rings
    /// extend outward from the origin in both directions, so this is the common case away from
    /// spawn, not an edge case.</summary>
    [Fact]
    public void CoarseRegionAtNegativeCoordinatesCountsItsOwnChildren()
    {
        var chunks = new ChunkManager(null!);
        var lods = new LodManager(null!);

        // Same ratio 2 as the positive case (level 1's RegionBlocks / level 0's), so coarse
        // region (1,-1,-1) spans level-0 (-2,-2), (-2,-1), (-1,-2) and (-1,-1).
        var coarse = CoarseHeadlessRegion(1, -1, -1);
        lods.AddRegion((1, -1, -1), coarse);

        lods.AddRegion((0, -2, -2), HeadlessRegion(-2, -2, 7));
        lods.AddRegion((0, -2, -1), HeadlessRegion(-2, -1, 7));
        lods.AddRegion((0, -1, -2), HeadlessRegion(-1, -2, 7));

        lods.UpdateCoverFlags(chunks, Vector3.Zero);
        Assert.False(coarse.IsBuried);

        // A neighbour one coarse region over, whose real parent is (1,-2,-1), so it must not
        // count here. A parent key built with a truncating divide collapses -3 and -2 onto the
        // same parent x (-3 / 2 == -1 == -2 / 2) and mis-buckets children like this one.
        lods.AddRegion((0, -3, -1), HeadlessRegion(-3, -1, 7));
        lods.UpdateCoverFlags(chunks, Vector3.Zero);
        Assert.False(coarse.IsBuried);

        lods.AddRegion((0, -1, -1), HeadlessRegion(-1, -1, 7));
        lods.UpdateCoverFlags(chunks, Vector3.Zero);
        Assert.True(coarse.IsBuried);
    }

    /// <summary>Covers item C11: TakeDirtyRegion now walks the dirty dictionary itself instead of
    /// materialising <c>.Keys</c>. The fact pins the drain's BEHAVIOUR — every pending region is
    /// handed out exactly once and the set then reports empty — not the enumeration mechanism: a
    /// test cannot observe the absence of the Keys snapshot's locks or its copy, only that the
    /// claim semantics are unchanged. The dirty regions are produced through the real path
    /// (FindClosestNeededRegion fills _queued, which is the only thing MarkEditedColumn will mark),
    /// so the producer half is exercised too.</summary>
    [Fact]
    public void DirtyRegionsDrainExactlyOnce()
    {
        var lods = new LodManager(null!);
        var cam = new Camera(null, null);
        cam.TestTeleport(Vector3.Zero, 0f, 0f);
        lods.AttachCamera(cam);

        var claimed = new HashSet<(int Level, int X, int Z)>();
        for (var i = 0; i < 8; i++)
        {
            var next = lods.FindClosestNeededRegion();
            Assert.NotNull(next);
            Assert.True(claimed.Add(next!.Value), "the scan handed out the same region twice");
        }

        lods.MarkEditedColumn(0, 0);

        var drained = new List<(int Level, int X, int Z)>();
        while (lods.TakeDirtyRegion() is { } key)
            drained.Add(key);

        Assert.NotEmpty(drained);
        Assert.All(drained, key => Assert.Contains(key, claimed));
        Assert.Equal(drained.Count, drained.Distinct().Count());
        Assert.Null(lods.TakeDirtyRegion());
    }

    /// <summary>Covers item C11's common case: the LodGen worker calls TakeDirtyRegion on every
    /// spin and the set is empty for nearly all of them, so the empty walk must stay correct (and,
    /// unobservably from here, allocate nothing). Behaviour only — the fact cannot see whether a
    /// key snapshot was taken.</summary>
    [Fact]
    public void AnEmptyDirtySetReturnsNull()
    {
        Assert.Null(new LodManager(null!).TakeDirtyRegion());
    }
}
