using System;
using System.Collections.Generic;

namespace silkgltriangle;

/// <summary>
/// First-fit free-list allocator over an integer "unit" space <c>[0, Capacity)</c>, backing the
/// shared chunk geometry buffers (<c>ChunkGeometryPool</c>). One unit is a fixed 64 vertices AND
/// 96 indices, so a single allocation covers the vertex buffer and the index buffer at the same
/// unit offset — quads are exactly 4 vertices to 6 indices, so the pairing wastes nothing and one
/// free list is enough for both buffers.
///
/// Why a free list in fixed units rather than a bump allocator or a per-chunk buffer:
/// chunks are remeshed constantly, so the space must be reusable; fixed units bound the
/// fragmentation (a hole is always a whole number of units and coalesces with its neighbours),
/// and first fit keeps the low offsets packed so growth stays rare.
///
/// The class deliberately touches no GL: offsets are deterministic, so the unit tests can assert
/// exact values, and the whole allocation policy is exercised headlessly.
/// </summary>
internal sealed class GeometryAllocator
{
    // Free blocks, kept sorted by offset and never adjacent to one another (Free coalesces).
    private readonly List<(int Offset, int Count)> _free = new();

    /// <param name="capacityUnits">Size of the unit space; must be positive.</param>
    public GeometryAllocator(int capacityUnits)
    {
        if (capacityUnits <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacityUnits), capacityUnits, "Capacity must be positive.");
        Capacity = capacityUnits;
        _free.Add((0, capacityUnits));
    }

    /// <summary>Total units in the space; only <see cref="Grow"/> changes it.</summary>
    public int Capacity { get; private set; }

    /// <summary>Units currently handed out.</summary>
    public int UsedUnits { get; private set; }

    /// <summary>Number of (coalesced) free blocks — a fragmentation gauge, and asserted by tests.</summary>
    public int FreeBlockCount => _free.Count;

    /// <summary>Size of the biggest free block, i.e. the largest allocation that can still succeed.</summary>
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

    /// <summary>
    /// First fit: hands out the front of the first free block large enough. Returns false and
    /// leaves the allocator untouched when nothing fits, so the caller can grow and retry.
    /// </summary>
    public bool TryAllocate(int units, out int offset)
    {
        if (units <= 0)
            throw new ArgumentOutOfRangeException(nameof(units), units, "Allocation size must be positive.");

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

    /// <summary>
    /// Returns a range to the free list, merging it with an immediately preceding and/or
    /// following free block so the list never holds two adjacent entries.
    /// </summary>
    /// <exception cref="InvalidOperationException">The range overlaps a free block (double free).</exception>
    public void Free(int offset, int units)
    {
        if (units <= 0)
            throw new ArgumentOutOfRangeException(nameof(units), units, "Freed size must be positive.");
        if (offset < 0 || offset + units > Capacity)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Freed range lies outside the unit space.");

        // Insertion point: the first block that starts after this range.
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

        // Coalesce forwards first so the backwards merge below sees the combined block.
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

    /// <summary>
    /// Extends the unit space after the backing buffers have been reallocated larger. The new
    /// tail merges with a trailing free block, so growing twice in a row does not leave a seam.
    /// </summary>
    public void Grow(int newCapacity)
    {
        if (newCapacity <= Capacity)
            throw new ArgumentOutOfRangeException(nameof(newCapacity), newCapacity, "Growth must increase the capacity.");

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
