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
}
