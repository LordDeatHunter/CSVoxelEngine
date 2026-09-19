using System;

namespace silkgltriangle;

/// <summary>
/// First-fit free-block allocator over an integer "unit" space <c>[0, Capacity)</c>, backing the
/// shared chunk geometry buffers (<c>ChunkGeometryPool</c>). One unit is a fixed 64 vertices AND
/// 96 indices, so a single allocation covers the vertex buffer and the index buffer at the same
/// unit offset — quads are exactly 4 vertices to 6 indices, so the pairing wastes nothing and one
/// free structure is enough for both buffers.
///
/// Why a free structure in fixed units rather than a bump allocator or a per-chunk buffer:
/// chunks are remeshed constantly, so the space must be reusable; fixed units bound the
/// fragmentation (a hole is always a whole number of units and coalesces with its neighbours),
/// and first fit keeps the low offsets packed so growth stays rare.
///
/// The free blocks live in an offset-keyed treap, each node carrying the maximum free
/// <c>Count</c> over its subtree (item D5). That augmentation is what keeps EXACT first fit —
/// the lowest-offset block large enough, not a best fit out of a size bucket — while making the
/// search logarithmic: descend left whenever the left subtree still holds a block big enough,
/// else take this node if it fits, else go right. A size-bucketed free list, D5's other
/// candidate, cannot reproduce those offsets and would have re-valued four pinned facts.
/// The measurement that justified the rewrite is in PERF_TODO.md under "Measurement: D5"; the
/// short version is that the flat list's scan reached 7.3M entries in a single mass-unload frame
/// on the OPAQUE pool, whose free list runs an order of magnitude longer than the LOD pool's.
///
/// The treap is keyed by <c>Offset</c> and heap-ordered by a priority hashed from the offset the
/// block had when it was created. Priorities are never recomputed: <see cref="TryAllocate"/>
/// shaves units off the FRONT of a block, which raises its key but can never move it past its
/// neighbour (free blocks are coalesced, so they are never adjacent), so the search order stays
/// correct and the heap order is untouched — and the allocation stays a single descent.
/// Ties in priority are broken by offset, so the structure is fully deterministic and the unit
/// tests can assert exact values.
///
/// Nodes are recycled through a private spare list, so a steady state of allocate/free churn
/// allocates nothing on the render thread. The treap is NOT concurrent and must not become so:
/// the allocator is render-thread-only by design (see <c>ChunkGeometryPool</c>'s class doc).
///
/// The class deliberately touches no GL: offsets are deterministic, so the unit tests can assert
/// exact values, and the whole allocation policy is exercised headlessly.
///
/// <c>ScanSteps</c>, <c>AllocatorCalls</c> and <c>PeakFreeBlockCount</c> are the diagnostics item
/// D5 was decided on. They ship unconditionally rather than behind an <c>#if DEBUG</c>: Release
/// is the only configuration measured or shipped here, and a conditional counter would be
/// untestable headlessly. Each one is a single integer add or compare per node visited or per
/// allocator call, with no allocation, so the instrument cannot become what a capture measures.
/// </summary>
internal sealed class GeometryAllocator
{
    /// <summary>One free block. Mutable and recycled, so churn allocates nothing.</summary>
    private sealed class Node
    {
        public int Offset;
        public int Count;

        /// <summary>Largest <see cref="Count"/> in this subtree — what makes first fit O(log n).</summary>
        public int MaxCount;

        /// <summary>Max-heap key, hashed from the offset at creation and never recomputed.</summary>
        public uint Priority;

        public Node? Left;
        public Node? Right;
    }

    private Node? _root;

    /// <summary>Recycled nodes, chained through <see cref="Node.Left"/>.</summary>
    private Node? _spare;

    private int _blockCount;

    /// <summary>Nodes visited by the call in progress; folded into <see cref="ScanSteps"/> once.</summary>
    private int _steps;

    /// <param name="capacityUnits">Size of the unit space; must be positive.</param>
    public GeometryAllocator(int capacityUnits)
    {
        if (capacityUnits <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacityUnits), capacityUnits, "Capacity must be positive.");
        Capacity = capacityUnits;
        _root = Rent(0, capacityUnits);
        _blockCount = 1;
        PeakFreeBlockCount = 1; // The initial whole-space block.
    }

    /// <summary>Total units in the space; only <see cref="Grow"/> changes it.</summary>
    public int Capacity { get; private set; }

    /// <summary>Units currently handed out.</summary>
    public int UsedUnits { get; private set; }

    /// <summary>Number of (coalesced) free blocks — a fragmentation gauge, and asserted by tests.</summary>
    public int FreeBlockCount => _blockCount;

    /// <summary>
    /// Free-structure nodes examined by <see cref="TryAllocate"/> and <see cref="Free"/> since
    /// construction — the direct, countable measure item D5 was decided on. Under the flat list
    /// this was the length of a linear scan; under the treap it is the length of the descents a
    /// call performs, which is what makes the before/after comparison in PERF_TODO.md a like-for-like
    /// count of the same quantity. <see cref="Grow"/> is deliberately excluded: it is one rightmost
    /// walk and a tail extension, not the search this item is about.
    /// </summary>
    public long ScanSteps { get; private set; }

    /// <summary>
    /// Cumulative <see cref="TryAllocate"/> + <see cref="Free"/> calls, so a capture can report
    /// scan steps per allocator call as well as per frame.
    /// </summary>
    public long AllocatorCalls { get; private set; }

    /// <summary>
    /// High-water <see cref="FreeBlockCount"/>. Updated inside <see cref="Free"/> AND inside
    /// <see cref="Grow"/>'s non-coalescing branch, because a teleport storm's peak is intra-frame
    /// (<c>LodManager.Update</c>'s retention loop and <c>ChunkManager.UnloadDistantChunks</c> are
    /// both unbounded per frame, so an end-of-frame sample reads the drained list) and a grow can
    /// raise the block count outside <see cref="Free"/>.
    /// </summary>
    public int PeakFreeBlockCount { get; private set; }

    /// <summary>
    /// Size of the biggest free block, i.e. the largest allocation that can still succeed. O(1):
    /// it is the root's subtree maximum. (Under the flat list this was an O(n) walk of every block.)
    /// </summary>
    public int LargestFreeBlock => _root?.MaxCount ?? 0;

    /// <summary>
    /// First fit: hands out the front of the LOWEST-OFFSET free block large enough, found by a
    /// single max-augmented descent. Returns false and leaves the allocator untouched when nothing
    /// fits, so the caller can grow and retry.
    /// </summary>
    public bool TryAllocate(int units, out int offset)
    {
        if (units <= 0)
            throw new ArgumentOutOfRangeException(nameof(units), units, "Allocation size must be positive.");

        _steps = 0;

        if (_root is null || _root.MaxCount < units)
        {
            offset = 0;
            // The root's subtree maximum settles a failing request outright, where the flat list
            // had to walk every block to prove that nothing fitted.
            ScanSteps += _root is null ? 0 : 1;
            AllocatorCalls++;
            return false;
        }

        _root = AllocateFrom(_root, units, out offset);
        UsedUnits += units;
        ScanSteps += _steps;
        AllocatorCalls++;
        return true;
    }

    /// <summary>
    /// Leftmost descent to the first-fit block. The caller guarantees <c>t.MaxCount &gt;= units</c>,
    /// which is what makes the right-hand recursion below safe: if neither the left subtree nor
    /// this node can hold the request, the right subtree must.
    /// </summary>
    private Node? AllocateFrom(Node t, int units, out int offset)
    {
        _steps++;

        // Anything in the left subtree has a lower offset, so it wins if it fits at all.
        if (t.Left is not null && t.Left.MaxCount >= units)
        {
            t.Left = AllocateFrom(t.Left, units, out offset);
            Update(t);
            return t;
        }

        if (t.Count >= units)
        {
            offset = t.Offset;
            if (t.Count == units)
            {
                var merged = Merge(t.Left, t.Right);
                Recycle(t);
                _blockCount--;
                return merged;
            }

            // Shaving the front raises the key but never past the successor — free blocks are
            // coalesced, so the next block starts strictly beyond this one's end. The priority is
            // left alone: the heap order is what the treap needs, and keeping it makes this a
            // single descent instead of a delete plus a re-insert.
            t.Offset += units;
            t.Count -= units;
            Update(t);
            return t;
        }

        t.Right = AllocateFrom(t.Right!, units, out offset);
        Update(t);
        return t;
    }

    /// <summary>
    /// Returns a range to the free structure, merging it with an immediately preceding and/or
    /// following free block so two adjacent blocks never coexist. One descent finds both
    /// neighbours; the merge cases then mutate a neighbour in place (and fix the subtree maxima
    /// along its path), and only the no-merge case inserts a node.
    /// </summary>
    /// <exception cref="InvalidOperationException">The range overlaps a free block (double free).</exception>
    public void Free(int offset, int units)
    {
        if (units <= 0)
            throw new ArgumentOutOfRangeException(nameof(units), units, "Freed size must be positive.");
        if (offset < 0 || offset + units > Capacity)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Freed range lies outside the unit space.");

        _steps = 0;

        // The search path for a key visits both its neighbours: the last node we passed going
        // right is the predecessor, the last we passed going left is the successor.
        Node? pred = null;
        Node? succ = null;
        var node = _root;
        while (node is not null)
        {
            _steps++;
            if (node.Offset < offset)
            {
                pred = node;
                node = node.Right;
            }
            else
            {
                succ = node;
                node = node.Left;
            }
        }

        // Free blocks are disjoint and sorted, so if anything overlaps this range then one of the
        // two neighbours does. Thrown before anything is mutated or counted, as the flat list did.
        if ((pred is not null && pred.Offset + pred.Count > offset) ||
            (succ is not null && succ.Offset < offset + units))
            throw new InvalidOperationException(
                $"Geometry range [{offset}, {offset + units}) is already free (double free).");

        var mergePred = pred is not null && pred.Offset + pred.Count == offset;
        var mergeSucc = succ is not null && offset + units == succ.Offset;

        if (mergePred && mergeSucc)
        {
            // Both sides close up: the successor's span moves into the predecessor and its node
            // leaves the tree. Read its Count before the removal recycles it.
            var combined = pred!.Count + units + succ!.Count;
            _root = RemoveNode(_root, succ.Offset);
            _blockCount--;
            pred.Count = combined;
            UpdatePath(_root, pred.Offset);
        }
        else if (mergePred)
        {
            pred!.Count += units;
            UpdatePath(_root, pred.Offset);
        }
        else if (mergeSucc)
        {
            // The successor's key drops to the freed offset. It stays above the predecessor's end
            // (there is no predecessor merge here, and an overlap already threw), so the search
            // order — and therefore the path just walked — is unchanged.
            succ!.Offset = offset;
            succ.Count += units;
            UpdatePath(_root, offset);
        }
        else
        {
            _root = InsertNode(_root, Rent(offset, units));
            _blockCount++;
        }

        UsedUnits -= units;
        ScanSteps += _steps;
        AllocatorCalls++;

        // After the merges, so the high-water mark records settled block counts rather than a
        // transient one-longer state.
        if (_blockCount > PeakFreeBlockCount) PeakFreeBlockCount = _blockCount;
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

        // The scan counters deliberately exclude Grow — a grow is one rightmost walk and a tail
        // extension, not the first-fit search item D5 is about — so _steps is reset here and never
        // folded into ScanSteps. The peak is NOT excluded: a non-adjacent tail block genuinely
        // lengthens the structure that TryAllocate and Free descend.
        _steps = 0;

        var last = _root;
        while (last?.Right is not null) last = last.Right;

        if (last is not null && last.Offset + last.Count == Capacity)
        {
            last.Count += added;
            UpdatePath(_root, last.Offset);
            Capacity = newCapacity;
            return;
        }

        _root = InsertNode(_root, Rent(Capacity, added));
        _blockCount++;
        if (_blockCount > PeakFreeBlockCount) PeakFreeBlockCount = _blockCount;
        Capacity = newCapacity;
    }

    // ---------------------------------------------------------------------------------------
    // Treap internals. Keyed by Offset, max-heap on Priority, augmented with MaxCount.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Priority for a block created at <paramref name="offset"/>: SplitMix64 with its standard
    /// constants and a fixed seed, so the structure is deterministic and tests stay exact-valued.
    /// </summary>
    private static uint PriorityFor(int offset)
    {
        var z = (ulong)(uint)offset + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (uint)(z >> 32);
    }

    /// <summary>Heap comparison. Ties break on offset, so no shape depends on insertion history.</summary>
    private static bool Outranks(Node x, Node y) =>
        x.Priority != y.Priority ? x.Priority > y.Priority : x.Offset < y.Offset;

    private static void Update(Node t)
    {
        var max = t.Count;
        if (t.Left is not null && t.Left.MaxCount > max) max = t.Left.MaxCount;
        if (t.Right is not null && t.Right.MaxCount > max) max = t.Right.MaxCount;
        t.MaxCount = max;
    }

    private Node Rent(int offset, int count)
    {
        Node n;
        if (_spare is null)
        {
            n = new Node();
        }
        else
        {
            n = _spare;
            _spare = n.Left;
        }

        n.Offset = offset;
        n.Count = count;
        n.MaxCount = count;
        n.Priority = PriorityFor(offset);
        n.Left = null;
        n.Right = null;
        return n;
    }

    private void Recycle(Node n)
    {
        n.Right = null;
        n.Left = _spare;
        _spare = n;
    }

    /// <summary>Inserts a node by key, restoring the heap order with rotations on the unwind.</summary>
    private Node InsertNode(Node? t, Node n)
    {
        if (t is null) return n;

        _steps++;
        if (n.Offset < t.Offset)
        {
            t.Left = InsertNode(t.Left, n);
            if (Outranks(t.Left, t)) t = RotateRight(t);
        }
        else
        {
            t.Right = InsertNode(t.Right, n);
            if (Outranks(t.Right, t)) t = RotateLeft(t);
        }

        Update(t);
        return t;
    }

    /// <summary>Removes the node with the given key and recycles it.</summary>
    private Node? RemoveNode(Node? t, int offset)
    {
        if (t is null) return null;

        _steps++;
        if (t.Offset == offset)
        {
            var merged = Merge(t.Left, t.Right);
            Recycle(t);
            return merged;
        }

        if (offset < t.Offset) t.Left = RemoveNode(t.Left, offset);
        else t.Right = RemoveNode(t.Right, offset);

        Update(t);
        return t;
    }

    /// <summary>Joins two subtrees whose keys are entirely ordered, keeping the heap order.</summary>
    private Node? Merge(Node? a, Node? b)
    {
        if (a is null) return b;
        if (b is null) return a;

        _steps++;
        if (Outranks(a, b))
        {
            a.Right = Merge(a.Right, b);
            Update(a);
            return a;
        }

        b.Left = Merge(a, b.Left);
        Update(b);
        return b;
    }

    /// <summary>
    /// Recomputes the subtree maxima from the root down to <paramref name="key"/>, after a node on
    /// that path has had its <c>Count</c> or <c>Offset</c> changed in place.
    /// </summary>
    private void UpdatePath(Node? t, int key)
    {
        if (t is null) return;

        _steps++;
        if (key < t.Offset) UpdatePath(t.Left, key);
        else if (key > t.Offset) UpdatePath(t.Right, key);
        Update(t);
    }

    private static Node RotateRight(Node t)
    {
        var l = t.Left!;
        t.Left = l.Right;
        l.Right = t;
        Update(t);
        Update(l);
        return l;
    }

    private static Node RotateLeft(Node t)
    {
        var r = t.Right!;
        t.Right = r.Left;
        r.Left = t;
        Update(t);
        Update(r);
        return r;
    }
}
