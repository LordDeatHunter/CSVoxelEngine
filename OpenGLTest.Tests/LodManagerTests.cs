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

    /// <summary>Covers item C12(c): a region unloading now swap-removes itself out of
    /// <c>_pendingCover</c> through its own <see cref="LodRegion.PendingCoverIndex"/> instead of a
    /// linear <c>List.Remove</c>, and the buried-flag pass resets every index as it drains the list.
    ///
    /// Two halves, because the two ways the bookkeeping can be wrong show up differently. The
    /// FIRST half is behavioural: no chunk is meshed anywhere, so every region the pass evaluates
    /// comes back unburied, and a region still reading the <c>true</c> sentinel afterwards is one
    /// the pass never looked at. An unloaded region is gone from RegionList, so <c>_pendingCover</c>
    /// is the only route by which the pass could still reach it — if the swap-remove dropped the
    /// wrong entry, the unloaded region would be evaluated and one survivor would not be.
    ///
    /// The SECOND half pins the reset at the Clear, which has no behavioural tell here (the pass's
    /// later loops re-reach any region still in RegionList whichever list it is on) and is asserted
    /// on the indices directly. Without the reset a region added before one pass and unloaded after
    /// a later one would swap-remove against a stale index and evict whichever region has since
    /// taken that slot: the fact removes the FIRST region of the earlier batch while two fresh ones
    /// are pending, so a stale index would move the second fresh region down to slot 0.</summary>
    [Fact]
    public void RemovingAPendingRegionLeavesTheOthersEvaluable()
    {
        var chunks = new ChunkManager(null!);
        var lods = new LodManager(null!);

        var first = HeadlessRegion(0, 0, 7);
        var middle = HeadlessRegion(1, 0, 7);
        var last = HeadlessRegion(2, 0, 7);
        lods.AddRegion((0, 0, 0), first);
        lods.AddRegion((0, 1, 0), middle);
        lods.AddRegion((0, 2, 0), last);
        Assert.Equal(0, first.PendingCoverIndex);
        Assert.Equal(1, middle.PendingCoverIndex);
        Assert.Equal(2, last.PendingCoverIndex);

        first.IsBuried = true;
        middle.IsBuried = true;
        last.IsBuried = true;

        // The unload path LodManager.Update runs: the key leaves Regions, the region leaves
        // RegionList and _pendingCover. Removing the MIDDLE entry is what exercises the swap.
        lods.Regions.TryRemove((0, 1, 0), out _);
        lods.RemoveRegion(middle);
        Assert.Equal(-1, middle.PendingCoverIndex);
        Assert.Equal(0, first.PendingCoverIndex);
        Assert.Equal(1, last.PendingCoverIndex);

        lods.UpdateCoverFlags(chunks, Vector3.Zero);
        Assert.False(first.IsBuried);
        Assert.False(last.IsBuried);
        Assert.True(middle.IsBuried);

        // Drained, so every surviving index went back to -1 ...
        Assert.Equal(-1, first.PendingCoverIndex);
        Assert.Equal(-1, last.PendingCoverIndex);

        // ... and a later add/remove pair therefore swaps against live indices, not stale ones.
        var fresh = HeadlessRegion(3, 0, 7);
        var fresher = HeadlessRegion(4, 0, 7);
        lods.AddRegion((0, 3, 0), fresh);
        lods.AddRegion((0, 4, 0), fresher);
        Assert.Equal(0, fresh.PendingCoverIndex);
        Assert.Equal(1, fresher.PendingCoverIndex);

        lods.Regions.TryRemove((0, 0, 0), out _);
        lods.RemoveRegion(first);
        Assert.Equal(0, fresh.PendingCoverIndex);
        Assert.Equal(1, fresher.PendingCoverIndex);

        fresh.IsBuried = true;
        fresher.IsBuried = true;
        lods.UpdateCoverFlags(chunks, Vector3.Zero);
        Assert.False(fresh.IsBuried);
        Assert.False(fresher.IsBuried);
        Assert.Equal(-1, fresh.PendingCoverIndex);
        Assert.Equal(-1, fresher.PendingCoverIndex);
    }

    /// <summary>Covers item C11: TakeDirtyRegion now walks the dirty dictionary itself instead of
    /// materialising <c>.Keys</c>. The fact pins the drain's BEHAVIOUR — every pending region is
    /// handed out exactly once and the set then reports empty — not the enumeration mechanism: a
    /// test cannot observe the absence of the Keys snapshot's locks or its copy, only that the
    /// claim semantics are unchanged. The dirty regions are produced through the real path
    /// (FindClosestNeededRegion fills _queued, which is the only thing MarkEditedColumn will mark),
    /// so the producer half is exercised too.
    ///
    /// Amended for item C5, and the amendment is a real behaviour change rather than a test fix:
    /// a claim from either path now registers its key in <c>_building</c> for the duration of the
    /// build, and TakeDirtyRegion deliberately REFUSES to hand out a region that is still being
    /// built (it skips it without removing, so the dirty mark survives for a later spin). The
    /// production loop releases in a <c>finally</c> the moment the build is enqueued, so the eight
    /// claims below are released before the drain — otherwise this would be draining a dirty set
    /// whose every entry is still in flight, and the correct answer to that is "nothing yet".
    /// What the fact pins is unchanged: each pending region is handed out exactly once.</summary>
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

        // Stands for "those eight builds finished": the worker's finally hands every claim back.
        foreach (var key in claimed)
            lods.ReleaseRegion(key);
        Assert.Equal(0, lods.BuildingClaimCount);

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

    /// <summary>Pins the LodGen throughput instrument item C5's capture reads:
    /// <see cref="LodManager.CompletedBuildCount"/> counts BUILDS and is cumulative, so it neither
    /// falls when a build is dropped out of range nor tracks the live region count. That is what
    /// separates it from <c>ChunkManager.LoadedChunkCount</c>, a net counter whose purge bias the
    /// C3 capture rule had to work around; a capture over this one can use a fixed window.
    /// The mesh data is rented from the manager's own pool and handed back by the apply pass, so
    /// the fact also leaves the rent/return contract balanced rather than leaking four lists.</summary>
    [Fact]
    public void CompletedBuildCountIsCumulative()
    {
        var lods = new LodManager(null!);
        Assert.Equal(0, lods.CompletedBuildCount);

        var span = LodRegion.Levels[0].RegionBlocks / Chunk.Size;
        var n = span * span;
        for (var i = 0; i < 3; i++)
        {
            lods.EnqueueCompletedMesh(new LodMeshData(0, 1000 + i, 1000,
                lods.MeshBuffers.RentVertices(), lods.MeshBuffers.RentIndices(),
                lods.MeshBuffers.RentTranslucentVertices(), lods.MeshBuffers.RentTranslucentIndices(),
                0f, 0f, new int[n], new int[n]));
        }

        Assert.Equal(3, lods.CompletedBuildCount);

        // Every one of them is far out of range of a camera at the origin, so the apply pass takes
        // the drop branch: no region is created and nothing is uploaded. The count must not move —
        // the worker still paid for those three builds.
        var camRegions = new[] { (0, 0), (0, 0), (0, 0) };
        lods.ApplyCompletedMeshes(camRegions);

        Assert.True(lods.Regions.IsEmpty);
        Assert.Empty(lods.RegionList);
        Assert.Equal(3, lods.CompletedBuildCount);
    }

    // ---------------------------------------------------------------------------------------
    // Item C5: the claim-and-release protocol, pinned single-threaded and deterministically.
    //
    // These five facts cover the PROTOCOL, not the pool: LodManager's claim paths now register
    // the key they hand out in `_building` gated on the TryAdd RESULT, roll back when they lose
    // that race, and check the in-flight cap BEFORE taking any claim. Every one of them runs on
    // one thread by construction, so none of them can be flaky and none of them proves anything
    // about scheduling — they pin the invariants a worker pool then relies on. The stress half of
    // the pairing (N real workers against one drain) lives in LodBuildPoolTests; a green run there
    // demonstrates only the absence of the races that machine happened to exercise, whereas these
    // facts fail deterministically the moment the protocol itself is weakened.
    // ---------------------------------------------------------------------------------------

    private static LodManager ClaimableManager()
    {
        var lods = new LodManager(null!);
        var cam = new Camera(null, null);
        cam.TestTeleport(Vector3.Zero, 0f, 0f);
        lods.AttachCamera(cam);
        return lods;
    }

    /// <summary>The block column at the centre of a region, which is the only column guaranteed to
    /// mark that region and nothing beside it: MarkEditedColumn widens the edit by
    /// SampleMargin + CellSize (3 blocks at level 0) and a column at the centre of a 64-block
    /// region is far inside that margin.</summary>
    private static (int X, int Z) CentreColumnOf((int Level, int X, int Z) key)
    {
        var blocks = LodRegion.Levels[key.Level].RegionBlocks;
        return (key.X * blocks + blocks / 2, key.Z * blocks + blocks / 2);
    }

    private static LodMeshData RentedEmptyMesh(LodManager lods, int level, int x, int z)
    {
        var span = LodRegion.Levels[level].RegionBlocks / Chunk.Size;
        var n = span * span;
        return new LodMeshData(level, x, z,
            lods.MeshBuffers.RentVertices(), lods.MeshBuffers.RentIndices(),
            lods.MeshBuffers.RentTranslucentVertices(), lods.MeshBuffers.RentTranslucentIndices(),
            0f, 0f, new int[n], new int[n]);
    }

    /// <summary>Item C5's blocker, pinned directly: while one worker holds a region, no second
    /// claim may hand out that same key — not even through the dirty path, which is the path that
    /// can re-offer it. MarkEditedColumn re-dirties any key still present in <c>_queued</c>, and a
    /// key stays in <c>_queued</c> for its whole build, so a region that is mid-build is exactly
    /// the kind of region the dirty set fills up with. If <c>_building</c> were an advisory mark
    /// rather than the TryAdd-gated gate it is, two builds of one region would run concurrently and
    /// apply in COMPLETION order — the older snapshot landing last leaves a stale LOD shell over a
    /// freshly mined column. The release then makes the key takeable again, so the refusal is a
    /// deferral and never a dropped edit.</summary>
    [Fact]
    public void TwoClaimsNeverYieldTheSameKeyWhileTheFirstIsHeld()
    {
        var lods = ClaimableManager();

        var claimed = lods.ClaimNextRegion();
        Assert.NotNull(claimed);
        var k = claimed!.Value;
        Assert.Equal(1, lods.BuildingClaimCount);

        var (cx, cz) = CentreColumnOf(k);
        lods.MarkEditedColumn(cx, cz);

        // Repeatedly, because the refusal must hold for every spin of every worker, not just the
        // first. The pre-filter SKIPS the held key without removing it, so once it is the only
        // dirty entry the drain reports empty — what must never happen is K coming back out.
        for (var i = 0; i < 8; i++)
        {
            var taken = lods.TakeDirtyRegion();
            Assert.True(taken is null || taken.Value != k,
                "TakeDirtyRegion handed out a region that is still being built");
        }

        Assert.Equal(1, lods.BuildingClaimCount);

        lods.ReleaseRegion(k);
        var again = lods.TakeDirtyRegion();
        Assert.NotNull(again);
        Assert.Equal(k, again!.Value);
    }

    /// <summary>The invariant behind the dirty path's loser branch: a mark that collides with a
    /// claim already held is DEFERRED, never consumed — the region is still rebuilt once its
    /// builder releases it. Dropping it instead would cost an edit outright, because the region's
    /// mesh would keep its pre-edit heights until something dirtied it again, which for a region
    /// that is already loaded is never.
    ///
    /// Which line this actually pins, stated honestly rather than implied by the name: from one
    /// thread the collision is always resolved by the <c>_building.ContainsKey</c> pre-filter,
    /// which SKIPS without removing, so it is the pre-filter that this fact fails on (verified by
    /// mutation — deleting it makes the drain consume the mark and the assertion below fires). The
    /// <c>TryAdd</c> loser's <c>_dirtyRegions.TryAdd</c> rollback enforces the same invariant on
    /// the path a second worker can reach, and is unreachable single-threaded by construction; it
    /// is LodBuildPoolTests, not this file, that can ever execute it.</summary>
    [Fact]
    public void ALostDirtyClaimIsReDirtiedNotDropped()
    {
        var lods = ClaimableManager();

        var claimed = lods.ClaimNextRegion();
        Assert.NotNull(claimed);
        var k = claimed!.Value;

        var (cx, cz) = CentreColumnOf(k);
        lods.MarkEditedColumn(cx, cz);

        // Drain to exhaustion while the claim is held. Nothing comes out — but the mark must
        // survive the walk, and a TryRemove that did not put it back would have consumed it here.
        var drained = new List<(int Level, int X, int Z)>();
        while (lods.TakeDirtyRegion() is { } key)
            drained.Add(key);
        Assert.DoesNotContain(k, drained);

        lods.ReleaseRegion(k);

        var after = lods.TakeDirtyRegion();
        Assert.NotNull(after);
        Assert.Equal(k, after!.Value);
    }

    /// <summary>Item C5's in-flight cap, and specifically its ORDERING: the cap is checked before
    /// any claim is taken, so a throttled spin leaves both claim sets exactly as it found them.
    /// Checking after the claim would either leak a <c>_queued</c> entry that nothing would ever
    /// build — a permanent hole in the horizon, since FindClosestNeededRegion's TryAdd would keep
    /// failing on it — or silently drop a dirty mark that had already been removed from the set.
    /// The dirty mark is observed directly (it is still takeable afterwards) and the needed path
    /// through <see cref="LodManager.QueuedClaimCount"/>, because FindClosestNeededRegion CLAIMS
    /// whatever it returns: "the key it would have returned" is not observable by calling it.</summary>
    [Fact]
    public void TheInFlightCapThrottlesBeforeAnyClaimIsTaken()
    {
        var lods = ClaimableManager();

        // Dirty a KNOWN key: MarkEditedColumn only marks regions present in _queued, and only the
        // needed-scan puts them there. ReleaseRegion drops the _building claim and leaves _queued
        // alone, which is what the worker's finally does once a build is enqueued.
        var claimed = lods.ClaimNextRegion();
        Assert.NotNull(claimed);
        var k = claimed!.Value;
        lods.ReleaseRegion(k);
        var (cx, cz) = CentreColumnOf(k);
        lods.MarkEditedColumn(cx, cz);

        for (var i = 0; i < WorldGenerationConfig.MaxLodBuildsInFlight; i++)
            lods.EnqueueCompletedMesh(RentedEmptyMesh(lods, 0, 1000 + i, 1000));
        Assert.Equal(WorldGenerationConfig.MaxLodBuildsInFlight, lods.PendingUploadCount);

        var queuedBefore = lods.QueuedClaimCount;
        Assert.Equal(LodBuildOutcome.Throttled, lods.TryBuildOneRegion());
        Assert.Equal(queuedBefore, lods.QueuedClaimCount);
        Assert.Equal(0, lods.BuildingClaimCount);

        // The dirty mark was never touched, so the rebuild is delayed and not lost.
        var stillDirty = lods.TakeDirtyRegion();
        Assert.NotNull(stillDirty);
        Assert.Equal(k, stillDirty!.Value);
    }

    /// <summary>Idle and Throttled must be distinguishable, because the worker loop sleeps
    /// GenerationDelayIdleMs = 200 for one and LodBuildThrottleDelayMs = 1 for the other. Reporting
    /// Throttled for an exhausted manager would spin every worker at ~1 ms forever once the horizon
    /// is full; reporting Idle for a throttled one would stall the streaming burst a fifth of a
    /// second per spin, which is the whole reason the two outcomes are separate values.</summary>
    [Fact]
    public void AnExhaustedManagerReportsIdleNotThrottled()
    {
        var lods = ClaimableManager();

        // Claim every offset of every level; the claims are deliberately NOT released, so the
        // manager is exhausted in exactly the way a set of busy workers would leave it.
        var claims = 0;
        while (lods.ClaimNextRegion() is not null)
        {
            claims++;
            Assert.True(claims < 4096, "the needed-region scan never ran out of regions to claim");
        }
        Assert.True(claims > 0, "the scan handed out nothing at all");

        Assert.Equal(0, lods.PendingUploadCount);
        Assert.Equal(LodBuildOutcome.Idle, lods.TryBuildOneRegion());
    }

    /// <summary>The protocol's base case: a claim followed by its release leaves nothing held and
    /// the key claimable again. That round trip is what the worker's <c>finally</c> guarantees on
    /// every path, including the throwing one — and the throwing path is covered by that
    /// <c>finally</c> in the source rather than by a fact here, honestly, because TryBuildOneRegion
    /// has no seam to inject a failing build through and adding one for this alone would buy a
    /// single assertion at the cost of permanent API surface on the build path.</summary>
    [Fact]
    public void ClaimAndReleaseRoundTrips()
    {
        var lods = ClaimableManager();

        var claimed = lods.ClaimNextRegion();
        Assert.NotNull(claimed);
        var k = claimed!.Value;
        Assert.Equal(1, lods.BuildingClaimCount);

        lods.ReleaseRegion(k);
        Assert.Equal(0, lods.BuildingClaimCount);

        // Claimable again through the dirty path, which is the one the release actually reopens:
        // the needed-scan would skip K anyway, since _queued still holds it.
        var (cx, cz) = CentreColumnOf(k);
        lods.MarkEditedColumn(cx, cz);
        var reclaimed = lods.ClaimNextRegion();
        Assert.NotNull(reclaimed);
        Assert.Equal(k, reclaimed!.Value);
        Assert.Equal(1, lods.BuildingClaimCount);
    }
}
