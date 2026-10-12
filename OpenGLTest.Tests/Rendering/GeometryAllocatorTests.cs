using System;
using System.Collections.Generic;
using VoxelEngine.Rendering;
using Xunit;

namespace VoxelEngine.Tests.Rendering;

/// <summary>
/// <c>GeometryAllocator</c> is the first-fit free list behind the shared chunk geometry buffers.
/// It is deliberately GL-free, so every allocation policy decision — first fit, coalescing,
/// growth — is asserted here against exact offsets rather than eyeballed in the renderer.
/// </summary>
public sealed class GeometryAllocatorTests
{
    [Fact]
    public void AllocatesFirstFitDeterministically()
    {
        var allocator = new GeometryAllocator(16);

        Assert.True(allocator.TryAllocate(1, out var a));
        Assert.True(allocator.TryAllocate(2, out var b));
        Assert.True(allocator.TryAllocate(3, out var c));

        Assert.Equal(0, a);
        Assert.Equal(1, b);
        Assert.Equal(3, c);
        Assert.Equal(6, allocator.UsedUnits);
    }

    [Fact]
    public void FreeIsReusedFirstFit()
    {
        var allocator = new GeometryAllocator(16);
        allocator.TryAllocate(1, out _);
        allocator.TryAllocate(2, out _);
        allocator.TryAllocate(3, out _);

        allocator.Free(1, 2);

        // The hole at 1 fits, so first fit takes it in preference to the untouched tail.
        Assert.True(allocator.TryAllocate(2, out var reused));
        Assert.Equal(1, reused);

        Assert.True(allocator.TryAllocate(1, out var tail));
        Assert.Equal(6, tail);
        Assert.Equal(7, allocator.UsedUnits);
    }

    [Fact]
    public void AdjacentFreesCoalesce()
    {
        var allocator = new GeometryAllocator(4);
        allocator.TryAllocate(1, out _);
        allocator.TryAllocate(1, out _);
        allocator.TryAllocate(1, out _);
        allocator.TryAllocate(1, out _);

        // Freed out of order: middle, forward-adjacent, backward-adjacent, then the tail.
        allocator.Free(1, 1);
        allocator.Free(2, 1);
        allocator.Free(0, 1);
        allocator.Free(3, 1);

        Assert.Equal(1, allocator.FreeBlockCount);
        Assert.Equal(allocator.Capacity, allocator.LargestFreeBlock);
        Assert.Equal(0, allocator.UsedUnits);

        Assert.True(allocator.TryAllocate(4, out var whole));
        Assert.Equal(0, whole);
    }

    [Fact]
    public void FreeCoalescesAcrossTheTailBlock()
    {
        var allocator = new GeometryAllocator(8);
        Assert.True(allocator.TryAllocate(3, out var head));
        Assert.True(allocator.TryAllocate(5, out var tail));
        Assert.Equal(0, head);
        Assert.Equal(3, tail);

        allocator.Free(tail, 5);
        Assert.Equal(1, allocator.FreeBlockCount);
        Assert.Equal(5, allocator.LargestFreeBlock);

        allocator.Free(head, 3);
        Assert.Equal(1, allocator.FreeBlockCount);
        Assert.Equal(8, allocator.LargestFreeBlock);
        Assert.Equal(0, allocator.UsedUnits);
    }

    [Fact]
    public void TryAllocateFailsWhenNothingFits()
    {
        var allocator = new GeometryAllocator(4);
        Assert.True(allocator.TryAllocate(3, out _));

        Assert.False(allocator.TryAllocate(2, out var offset));
        Assert.Equal(0, offset);

        // A failed allocation must leave the free list exactly as it was.
        Assert.Equal(3, allocator.UsedUnits);
        Assert.Equal(1, allocator.FreeBlockCount);
        Assert.True(allocator.TryAllocate(1, out var last));
        Assert.Equal(3, last);
    }

    [Fact]
    public void GrowExtendsTheTailAndCoalesces()
    {
        var full = new GeometryAllocator(4);
        Assert.True(full.TryAllocate(4, out var all));
        Assert.Equal(0, all);
        Assert.False(full.TryAllocate(1, out _));

        full.Grow(8);
        Assert.Equal(8, full.Capacity);
        Assert.True(full.TryAllocate(4, out var grown));
        Assert.Equal(4, grown);

        full.Free(0, 4);
        full.Free(4, 4);
        Assert.Equal(1, full.FreeBlockCount);
        Assert.Equal(8, full.LargestFreeBlock);
        Assert.Equal(0, full.UsedUnits);

        // A trailing free block absorbs the new space instead of leaving a seam at the old end.
        var partial = new GeometryAllocator(4);
        Assert.True(partial.TryAllocate(2, out var head));
        Assert.Equal(0, head);
        partial.Grow(8);
        Assert.Equal(1, partial.FreeBlockCount);
        Assert.Equal(6, partial.LargestFreeBlock);
    }

    [Fact]
    public void DoubleFreeThrows()
    {
        var allocator = new GeometryAllocator(8);
        Assert.True(allocator.TryAllocate(2, out var offset));

        allocator.Free(offset, 2);
        Assert.Throws<InvalidOperationException>(() => allocator.Free(offset, 2));

        Assert.Throws<ArgumentOutOfRangeException>(() => allocator.Free(-1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => allocator.Free(7, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => allocator.Free(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeometryAllocator(0));
    }

    // -------------------------------------------------------------------------------------
    // Item R10: LOD regions now stream through a shared ChunkGeometryPool, which makes this
    // allocator the thing that absorbs region churn. The three facts below are the GL-free
    // half of R10's churn claim: what a rebuild costs the free list (nothing, after the
    // free-before-upload reorder in LodManager.ApplyCompletedMeshes), what a full stream-in /
    // stream-out cycle leaves behind (nothing), and how long the free list Free() linearly
    // scans actually gets mid-teleport. They are exact numbers rather than estimates because
    // item D5 is about that scan and needs a figure to argue against.
    // -------------------------------------------------------------------------------------

    /// <summary>The free-before-upload order in <c>LodManager.ApplyCompletedMeshes</c> only pays
    /// off if a same-sized reallocation actually reuses the hole, which first fit guarantees:
    /// the hole is the first block large enough, and an exact fit removes the block outright.
    /// So a rebuild of unchanged size leaves the free list byte-for-byte as it found it.</summary>
    [Fact]
    public void ASameSizeReallocationAfterAFreeReusesTheSameOffset()
    {
        var allocator = new GeometryAllocator(64);
        Assert.True(allocator.TryAllocate(5, out var first));
        Assert.True(allocator.TryAllocate(7, out var middle));
        Assert.True(allocator.TryAllocate(3, out var last));
        Assert.Equal(0, first);
        Assert.Equal(5, middle);
        Assert.Equal(12, last);

        var blocksBefore = allocator.FreeBlockCount;
        var usedBefore = allocator.UsedUnits;
        Assert.Equal(1, blocksBefore);
        Assert.Equal(15, usedBefore);

        allocator.Free(middle, 7);
        Assert.Equal(2, allocator.FreeBlockCount);

        Assert.True(allocator.TryAllocate(7, out var rebuilt));
        Assert.Equal(middle, rebuilt);
        Assert.Equal(blocksBefore, allocator.FreeBlockCount);
        Assert.Equal(usedBefore, allocator.UsedUnits);
    }

    // The resident-ceiling LOD set: LodManager generates regions inside RegionRadius but only
    // drops them past RegionRadius + 1, so 253 offsets x 3 levels = 759 regions can be resident
    // at once (pinned from LodRegion.Levels by LodGeometryPoolTests). Each one holds two slices,
    // a terrain shell and a sea sheet, and both floor at 64 units.
    private const int ResidentRegions = 759;
    private const int SliceUnits = 64;

    private static GeometryAllocator FullResidentSet()
    {
        var allocator = new GeometryAllocator(ResidentRegions * SliceUnits * 2);
        for (var i = 0; i < ResidentRegions; i++)
        {
            Assert.True(allocator.TryAllocate(SliceUnits, out var shell));
            Assert.True(allocator.TryAllocate(SliceUnits, out var sheet));
            Assert.Equal(i * SliceUnits * 2, shell);
            Assert.Equal(i * SliceUnits * 2 + SliceUnits, sheet);
        }

        Assert.Equal(0, allocator.FreeBlockCount);
        Assert.Equal(allocator.Capacity, allocator.UsedUnits);
        return allocator;
    }

    /// <summary>A far teleport unloads the whole resident LOD set — <c>LodManager.Update</c>'s
    /// unload loop is unbounded per frame — and the departures arrive in region-list order, not
    /// allocation order. Coalescing has to survive that: whatever order the 1518 slices come back
    /// in, the pool must end as one whole free block, or the next stream-in fragments forever.
    /// The order here is the fixed permutation i * 617 mod 1518 (617 is coprime with 1518), so
    /// the fact is a shuffle without depending on any Random implementation.</summary>
    [Fact]
    public void AFullStreamInAndOutCycleCoalescesBackToOneBlock()
    {
        var allocator = FullResidentSet();
        var slices = ResidentRegions * 2;

        for (var i = 0; i < slices; i++)
            allocator.Free(i * 617 % slices * SliceUnits, SliceUnits);

        Assert.Equal(1, allocator.FreeBlockCount);
        Assert.Equal(allocator.Capacity, allocator.LargestFreeBlock);
        Assert.Equal(0, allocator.UsedUnits);

        // And the reclaimed space is genuinely one run, not one run by arithmetic only.
        Assert.True(allocator.TryAllocate(allocator.Capacity, out var whole));
        Assert.Equal(0, whole);
    }

    /// <summary>How long the list <c>Free</c> linearly scans really gets, pinned rather than
    /// estimated (item D5). Half the resident set gone, worst case for fragmentation because
    /// every survivor separates two holes: each departed region's shell and sheet coalesce into
    /// one 128-unit block and nothing else merges, so the free list holds exactly one block per
    /// departed region. Of the 759 resident regions 380 have even indices, so that is 380 blocks
    /// — and each of the remaining 758 frees walks that list from the front.</summary>
    [Fact]
    public void MidTeleportFragmentationPeaksAtAKnownBlockCount()
    {
        var allocator = FullResidentSet();

        var departed = 0;
        for (var i = 0; i < ResidentRegions; i += 2)
        {
            allocator.Free(i * SliceUnits * 2, SliceUnits);
            allocator.Free(i * SliceUnits * 2 + SliceUnits, SliceUnits);
            departed++;
        }

        Assert.Equal(380, departed);
        Assert.Equal(380, allocator.FreeBlockCount);
        Assert.Equal(SliceUnits * 2, allocator.LargestFreeBlock);
        Assert.Equal((ResidentRegions - departed) * SliceUnits * 2, allocator.UsedUnits);

        // A survivor's own rebuild still fits without growing, because a whole region-sized
        // hole is exactly what a departed neighbor leaves.
        Assert.True(allocator.TryAllocate(SliceUnits * 2, out var reused));
        Assert.Equal(0, reused);
        Assert.Equal(379, allocator.FreeBlockCount);
    }

    // -------------------------------------------------------------------------------------
    // Item D5's instrument. ScanSteps / AllocatorCalls / PeakFreeBlockCount count the thing the
    // item is actually about — free-list entries walked — because the perf-fly window is
    // saturated and a timing would isolate nothing. The three facts below pin the counters
    // exactly: what a hand-computable scan costs, what the pinned mid-teleport peak costs, and
    // that adding the counters moved no offset. They ship unconditionally (see the allocator's
    // class doc): an #if-guarded counter would be untestable headlessly.
    // -------------------------------------------------------------------------------------

    /// <summary>Hand-computed, so the counter is pinned to arithmetic rather than to itself.
    ///
    /// RE-VALUED by item D5's treap (the fact's previous wording said that any O(log n) policy
    /// would re-value it, and this is that re-valuation). A step is now one TREE NODE visited, and
    /// a call performs one or two descents instead of one linear walk, so every delta below is
    /// derived from the shape of the tree at that moment rather than from the length of a list.
    ///
    /// The five opening allocations run against a single-node tree and each shaves the front off
    /// that node: 1 node visited each, 5 steps — unchanged, because a one-block free structure is
    /// the same whichever way it is stored. <c>Free(0, 1)</c> costs 2: one node for the neighbor
    /// descent (the 49-unit tail, which neither abuts nor overlaps) and one for the insert descent.
    /// <c>Free(3, 3)</c> costs 4: the tree now holds two nodes, and both the neighbor descent and
    /// the insert descent walk both of them — 11 so far, with the blocks
    /// <c>(0,1), (3,3), (15,49)</c>. Then a <c>TryAllocate(4)</c> costs 2, NOT the 3 the flat list
    /// charged: it visits the 1-unit block, whose subtree maximum sends it right, and then the
    /// 49-unit tail it takes — the 3-unit hole is never looked at, because the augmented maximum
    /// already ruled its subtree out. That elision is the whole point of the structure. Finally
    /// <c>Free(6, 4)</c> costs 6: a three-node neighbor descent finds the 3-unit block abutting
    /// the freed range, so nothing is inserted — the block is widened in place and a three-node
    /// path walk restores the subtree maxima above it. Total 19.</summary>
    [Fact]
    public void ScanStepsCountEveryFreeListEntryTheScanTouches()
    {
        var allocator = new GeometryAllocator(64);
        Assert.Equal(0, allocator.ScanSteps);
        Assert.Equal(0, allocator.AllocatorCalls);
        Assert.Equal(1, allocator.PeakFreeBlockCount);

        // One block in the tree, so the very first allocation visits exactly one node.
        Assert.True(allocator.TryAllocate(1, out var a));
        Assert.Equal(1, allocator.ScanSteps);
        Assert.True(allocator.TryAllocate(2, out var b));
        Assert.True(allocator.TryAllocate(3, out var c));
        Assert.True(allocator.TryAllocate(4, out var d));
        Assert.True(allocator.TryAllocate(5, out var e));
        Assert.Equal(0, a);
        Assert.Equal(1, b);
        Assert.Equal(3, c);
        Assert.Equal(6, d);
        Assert.Equal(10, e);
        Assert.Equal(5, allocator.ScanSteps);

        // Two non-adjacent holes, deliberately too small for the allocation below. One node in
        // the tree for the first free (neighbor descent + insert descent), two for the second.
        allocator.Free(a, 1);
        Assert.Equal(7, allocator.ScanSteps);
        allocator.Free(c, 3);
        Assert.Equal(11, allocator.ScanSteps);
        Assert.Equal(3, allocator.FreeBlockCount);

        // Takes the tail WITHOUT looking at the 3-unit hole: the subtree maximum rules that
        // branch out, so the descent visits 2 nodes where the flat list examined 3 entries.
        var beforeAllocate = allocator.ScanSteps;
        Assert.True(allocator.TryAllocate(4, out var skipper));
        Assert.Equal(15, skipper);
        Assert.Equal(2, allocator.ScanSteps - beforeAllocate);

        // Abuts the 3-unit hole, so the block is widened in place and nothing is inserted: a
        // three-node neighbor descent plus a three-node walk to restore the subtree maxima.
        var beforeFree = allocator.ScanSteps;
        allocator.Free(d, 4);
        Assert.Equal(6, allocator.ScanSteps - beforeFree);

        Assert.Equal(19, allocator.ScanSteps);
        Assert.Equal(9, allocator.AllocatorCalls);
        Assert.Equal(3, allocator.PeakFreeBlockCount);
        Assert.Equal(3, allocator.FreeBlockCount);
        Assert.Equal(11, allocator.UsedUnits);
    }

    /// <summary>The scan cost of the pinned mid-teleport peak, derived and then asserted.
    ///
    /// Streaming the full resident set in still costs exactly one step per allocation: the free
    /// structure is a single block throughout, so the first-fit descent stops at the root every
    /// time — 1518 steps for 1518 slices, unchanged by item D5's rewrite.
    ///
    /// The departure storm is the half D5 re-valued. Under the flat list every free arrived at a
    /// HIGHER offset than every block already present, so the insertion loop never broke and
    /// walked the whole list: with k blocks present the shell free cost k steps and the sheet free
    /// k + 1, and Sum(2k+1) over k = 0..379 came to 380^2 = 144400 steps. That quadratic is the
    /// number PERF_TODO's "Measurement: D5" rule set its LOD bar against.
    ///
    /// The treap turns the same storm into 8372 steps, a 17.2x reduction, and the derivation is
    /// the harmonic sum rather than the arithmetic one. The frees still arrive in ascending offset
    /// order, so every descent walks to the largest key — the tree's right spine, whose expected
    /// length over k random priorities is the harmonic number H_k, not the k entries a list walk
    /// charged. Each departed region pays four such descents (two frees, and each free walks the
    /// neighbor path and then either an insert path or a maxima-restoring path), so the storm
    /// totals about 4 * Sum(H_k) for k = 1..380 = 4 * (381 * H_380 - 380) ~= 8415 against the 8372
    /// measured here — the residual being the difference between the expected spine length and the
    /// one this fixed priority hash actually produces.
    ///
    /// 8372 is asserted exactly rather than bounded, for the same reason 144400 was: if a future
    /// change makes a descent stop being logarithmic, this fact is what says so.</summary>
    [Fact]
    public void ScanStepsAtTheMidTeleportPeakArePinned()
    {
        var allocator = FullResidentSet();

        // One block in the structure for the whole stream-in, so one step per allocation.
        Assert.Equal(ResidentRegions * 2, allocator.ScanSteps);
        Assert.Equal(ResidentRegions * 2, allocator.AllocatorCalls);
        Assert.Equal(1, allocator.PeakFreeBlockCount);

        var streamIn = allocator.ScanSteps;
        var departed = 0;
        for (var i = 0; i < ResidentRegions; i += 2)
        {
            allocator.Free(i * SliceUnits * 2, SliceUnits);
            allocator.Free(i * SliceUnits * 2 + SliceUnits, SliceUnits);
            departed++;
        }

        Assert.Equal(380, departed);
        Assert.Equal(8372, allocator.ScanSteps - streamIn);

        // And it is genuinely sub-quadratic: the flat list charged 380^2 for this same storm.
        Assert.True(allocator.ScanSteps - streamIn < departed * departed / 8);
        Assert.Equal(380, allocator.PeakFreeBlockCount);
        Assert.Equal(380, allocator.FreeBlockCount);
        Assert.Equal(ResidentRegions * 2 + departed * 2, allocator.AllocatorCalls);
    }

    /// <summary>The counters are pure additions — one integer add or compare after the scan loop
    /// has already decided, never inside it — so not one offset may move. This fact replays the
    /// sequences of <c>AllocatesFirstFitDeterministically</c> and <c>FreeIsReusedFirstFit</c> and
    /// asserts their offsets again; it is the tripwire if anyone ever folds the accumulation into
    /// the loop body and changes where the scan stops.</summary>
    [Fact]
    public void TheInstrumentDoesNotMoveASingleOffset()
    {
        var firstFit = new GeometryAllocator(16);
        Assert.True(firstFit.TryAllocate(1, out var a));
        Assert.True(firstFit.TryAllocate(2, out var b));
        Assert.True(firstFit.TryAllocate(3, out var c));
        Assert.Equal(0, a);
        Assert.Equal(1, b);
        Assert.Equal(3, c);
        Assert.Equal(6, firstFit.UsedUnits);

        var reuse = new GeometryAllocator(16);
        reuse.TryAllocate(1, out _);
        reuse.TryAllocate(2, out _);
        reuse.TryAllocate(3, out _);

        reuse.Free(1, 2);

        Assert.True(reuse.TryAllocate(2, out var reused));
        Assert.Equal(1, reused);
        Assert.True(reuse.TryAllocate(1, out var tail));
        Assert.Equal(6, tail);
        Assert.Equal(7, reuse.UsedUnits);

        // And the counters did run, so the assertions above are not passing vacuously.
        Assert.True(firstFit.ScanSteps > 0);
        Assert.True(reuse.AllocatorCalls > 0);
    }

    // -------------------------------------------------------------------------------------
    // Item D5's landed change: the flat, linearly-scanned free list became an offset-keyed
    // treap augmented with the maximum free Count over each subtree. The two facts below are
    // the pair that has to be true for that to have been worth doing — the search really is
    // logarithmic, and the offsets it hands out are the ones the flat list handed out. The
    // second is the honest proof of the latter: the ten facts above passing is necessary but
    // not sufficient, since they exercise a handful of fixed sequences.
    // -------------------------------------------------------------------------------------

    /// <summary>The descent is logarithmic in the number of free blocks, asserted at the same
    /// 380-block mid-teleport ceiling the rest of this file pins.
    ///
    /// Three separate costs, all bounded by the tree height rather than the block count:
    /// a partial fit on the lowest block walks the left spine (5 nodes here, against the 1 entry
    /// a flat first fit would have charged for the same block — first fit is cheap when it wins
    /// early and ruinous when it does not); an exact fit on that same block costs the same 5,
    /// because the minimum node has no left child and joining its children is free; and a request
    /// nothing can satisfy costs exactly 1, the root's subtree maximum, where the flat list had to
    /// walk all 380 blocks to prove the same thing.
    ///
    /// The bound asserted alongside the exact figures is 2 * ceil(log2(380)) = 18. A treap's
    /// expected root-to-node path is about 2 * ln(n) ~= 11.9 at this size and its left spine about
    /// H_380 ~= 6.5, so 5 sits comfortably inside a bound that a linear scan could never meet.</summary>
    [Fact]
    public void TheFreeListDescentVisitsLogarithmicallyManyNodes()
    {
        const int logBound = 18; // 2 * ceil(log2(380))

        var partial = MidTeleportPeak();
        var beforePartial = partial.ScanSteps;
        Assert.True(partial.TryAllocate(SliceUnits, out var half));
        Assert.Equal(0, half);
        Assert.Equal(5, partial.ScanSteps - beforePartial);
        Assert.True(partial.ScanSteps - beforePartial <= logBound);

        var exact = MidTeleportPeak();
        var beforeExact = exact.ScanSteps;
        Assert.True(exact.TryAllocate(SliceUnits * 2, out var whole));
        Assert.Equal(0, whole);
        Assert.Equal(5, exact.ScanSteps - beforeExact);
        Assert.True(exact.ScanSteps - beforeExact <= logBound);
        Assert.Equal(379, exact.FreeBlockCount);

        // Nothing at this peak is bigger than one departed region, so this cannot succeed — and
        // the root's augmented maximum says so without descending.
        var failing = MidTeleportPeak();
        Assert.Equal(SliceUnits * 2, failing.LargestFreeBlock);
        var beforeFailure = failing.ScanSteps;
        Assert.False(failing.TryAllocate(SliceUnits * 2 + 1, out var none));
        Assert.Equal(0, none);
        Assert.Equal(1, failing.ScanSteps - beforeFailure);
        Assert.Equal(380, failing.FreeBlockCount);
    }

    /// <summary>The 380-block mid-teleport peak of
    /// <see cref="MidTeleportFragmentationPeaksAtAKnownBlockCount"/>, as a fixture.</summary>
    private static GeometryAllocator MidTeleportPeak()
    {
        var allocator = FullResidentSet();
        for (var i = 0; i < ResidentRegions; i += 2)
        {
            allocator.Free(i * SliceUnits * 2, SliceUnits);
            allocator.Free(i * SliceUnits * 2 + SliceUnits, SliceUnits);
        }

        Assert.Equal(380, allocator.FreeBlockCount);
        return allocator;
    }

    /// <summary>The policy did not change — offset for offset, against the flat linear first fit
    /// the treap replaced, reimplemented below exactly as it stood before item D5.
    ///
    /// The ten facts above this one pin specific sequences and would all pass under a policy that
    /// merely happened to agree on them; a size-bucketed free list, item D5's other candidate,
    /// fails several of them precisely because it hands out a best fit out of a bucket rather than
    /// the lowest-offset block that fits. This fact is the general claim instead: four thousand
    /// interleaved allocations, frees and growths driven by a fixed SplitMix64 stream, with both
    /// implementations stepped in lockstep and compared on every offset, every success/failure
    /// verdict, and — after each step — used units, block count and largest block.
    ///
    /// The driver is deliberately fragmenting and deliberately allocation-heavy: sizes 1..9 into a
    /// space that starts at 64 units, two allocations for every free, frees chosen from anywhere in
    /// the live set rather than in order, and a doubling grow on each exhaustion, which is what
    /// ChunkGeometryPool.Upload does. Six grows in, the pool stops growing and the replay spends
    /// the rest of its run against a nearly full, heavily fragmented space, which is where two
    /// placement policies are least likely to agree by accident. The non-vacuity assertions at the
    /// end pin that it really did fragment, really did exhaust the space and really did grow, so it
    /// cannot quietly degenerate into a bump allocator that both implementations agree on.</summary>
    [Fact]
    public void EveryOffsetTheFlatListProducedIsReproduced()
    {
        var tree = new GeometryAllocator(64);
        var flat = new FlatFirstFit(64);
        var live = new List<(int Offset, int Units)>();

        var state = 0xD5D5D5D5D5D5D5D5UL;
        var allocations = 0;
        var frees = 0;
        var failures = 0;
        var grows = 0;
        var peakBlocks = 0;

        for (var step = 0; step < 4000; step++)
        {
            if (live.Count > 0 && NextRandom(ref state) % 3 == 0)
            {
                var index = (int)(NextRandom(ref state) % (ulong)live.Count);
                var victim = live[index];
                live.RemoveAt(index);
                tree.Free(victim.Offset, victim.Units);
                flat.Free(victim.Offset, victim.Units);
                frees++;
            }
            else
            {
                var units = (int)(NextRandom(ref state) % 9) + 1;
                var treeFitted = tree.TryAllocate(units, out var treeOffset);
                var flatFitted = flat.TryAllocate(units, out var flatOffset);
                Assert.Equal(flatFitted, treeFitted);
                Assert.Equal(flatOffset, treeOffset);

                if (!treeFitted && grows < 6)
                {
                    // Exhaustion: grow both the way ChunkGeometryPool.Upload does, and retry.
                    failures++;
                    grows++;
                    tree.Grow(tree.Capacity * 2);
                    flat.Grow(flat.Capacity * 2);
                    treeFitted = tree.TryAllocate(units, out treeOffset);
                    flatFitted = flat.TryAllocate(units, out flatOffset);
                    Assert.Equal(flatFitted, treeFitted);
                    Assert.Equal(flatOffset, treeOffset);
                }

                if (treeFitted)
                {
                    live.Add((treeOffset, units));
                    allocations++;
                }
                else
                {
                    failures++;
                }
            }

            Assert.Equal(flat.Capacity, tree.Capacity);
            Assert.Equal(flat.UsedUnits, tree.UsedUnits);
            Assert.Equal(flat.FreeBlockCount, tree.FreeBlockCount);
            Assert.Equal(flat.LargestFreeBlock, tree.LargestFreeBlock);
            if (tree.FreeBlockCount > peakBlocks) peakBlocks = tree.FreeBlockCount;
        }

        // Draining it completely must still agree, including the final coalesce to one block.
        foreach (var remaining in live)
        {
            tree.Free(remaining.Offset, remaining.Units);
            flat.Free(remaining.Offset, remaining.Units);
        }

        Assert.Equal(flat.FreeBlockCount, tree.FreeBlockCount);
        Assert.Equal(1, tree.FreeBlockCount);
        Assert.Equal(tree.Capacity, tree.LargestFreeBlock);
        Assert.Equal(0, tree.UsedUnits);

        // The replay was not vacuous: it fragmented, it ran out, and it grew.
        Assert.True(allocations > 1000, $"only {allocations} allocations");
        Assert.True(frees > 500, $"only {frees} frees");
        Assert.True(failures > 0, "the replay never exhausted the space");
        Assert.Equal(6, grows);
        Assert.True(peakBlocks > 20, $"the replay only ever reached {peakBlocks} free blocks");
    }

    /// <summary>SplitMix64, so the replay above depends on no Random implementation.</summary>
    private static ulong NextRandom(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        var z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>
    /// The flat, offset-sorted, linearly-scanned free list <c>GeometryAllocator</c> used before
    /// item D5 — copied verbatim from that version (minus the diagnostics, which do not affect
    /// placement) so the replay above compares the treap against the real previous policy rather
    /// than against a fresh idea of what first fit ought to do.
    /// </summary>
    private sealed class FlatFirstFit
    {
        private readonly List<(int Offset, int Count)> _free = new();

        public FlatFirstFit(int capacityUnits)
        {
            Capacity = capacityUnits;
            _free.Add((0, capacityUnits));
        }

        public int Capacity { get; private set; }

        public int UsedUnits { get; private set; }

        public int FreeBlockCount => _free.Count;

        public int LargestFreeBlock
        {
            get
            {
                var largest = 0;
                foreach (var block in _free)
                    if (block.Count > largest)
                        largest = block.Count;
                return largest;
            }
        }

        public bool TryAllocate(int units, out int offset)
        {
            for (var i = 0; i < _free.Count; i++)
            {
                var block = _free[i];
                if (block.Count < units) continue;

                offset = block.Offset;
                if (block.Count == units) _free.RemoveAt(i);
                else _free[i] = (block.Offset + units, block.Count - units);
                UsedUnits += units;
                return true;
            }

            offset = 0;
            return false;
        }

        public void Free(int offset, int units)
        {
            var index = _free.Count;
            for (var i = 0; i < _free.Count; i++)
            {
                var block = _free[i];
                if (block.Offset < offset + units && offset < block.Offset + block.Count)
                    throw new InvalidOperationException(
                        $"Geometry range [{offset}, {offset + units}) is already free (double free).");
                if (block.Offset > offset)
                {
                    index = i;
                    break;
                }
            }

            _free.Insert(index, (offset, units));
            UsedUnits -= units;

            if (index + 1 < _free.Count)
            {
                var next = _free[index + 1];
                if (offset + units == next.Offset)
                {
                    _free[index] = (offset, units + next.Count);
                    _free.RemoveAt(index + 1);
                }
            }
            if (index > 0)
            {
                var prev = _free[index - 1];
                if (prev.Offset + prev.Count == offset)
                {
                    _free[index - 1] = (prev.Offset, prev.Count + _free[index].Count);
                    _free.RemoveAt(index);
                }
            }
        }

        public void Grow(int newCapacity)
        {
            var added = newCapacity - Capacity;
            if (_free.Count > 0)
            {
                var last = _free[^1];
                if (last.Offset + last.Count == Capacity)
                {
                    _free[^1] = (last.Offset, last.Count + added);
                    Capacity = newCapacity;
                    return;
                }
            }

            _free.Add((Capacity, added));
            Capacity = newCapacity;
        }
    }
}
