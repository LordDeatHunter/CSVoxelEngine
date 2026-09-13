using System;
using Xunit;

namespace silkgltriangle.Tests;

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
        // hole is exactly what a departed neighbour leaves.
        Assert.True(allocator.TryAllocate(SliceUnits * 2, out var reused));
        Assert.Equal(0, reused);
        Assert.Equal(379, allocator.FreeBlockCount);
    }
}
