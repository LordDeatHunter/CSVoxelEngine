using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace VoxelEngine.Chunks;

/// <summary>
/// The block ids of one chunk, palette-compressed. A chunk's cells are stored in one of three
/// modes, chosen by the number of distinct ids it holds: <see cref="StorageMode.Uniform"/> (one id,
/// no array: all-air sky, solid deep stone, hell rock), <see cref="StorageMode.Indexed"/> (a palette
/// of up to <see cref="PaletteCapacity"/> ids plus one index byte per cell, the same 4 KB as a byte
/// per cell) and <see cref="StorageMode.Direct"/> (one ushort per cell, only past
/// <see cref="PaletteCapacity"/> distinct ids). A read is one branch on the mode and a load or two;
/// there is no bit-packing. Cells are in <see cref="Chunk"/> Idx order. GL-free and content-free.
///
/// <para><b>1. The mode is fixed per object.</b> It follows from which of the readonly array
/// fields are set, so it never changes under a reader that holds the object.</para>
///
/// <para><b>2. One writer, lock-free readers.</b> <see cref="Set"/> is called by a single owner
/// thread at a time (the generation worker before the chunk is ready, the main thread after);
/// <see cref="Get"/>, <see cref="CopyRun"/> and <see cref="IdSpan"/> may run on any thread
/// concurrently with it and take no lock.</para>
///
/// <para><b>3. In-mode write order.</b> An Indexed write that needs a new palette entry stores the
/// palette slot, then publishes the count, then stores the cell's index with
/// <c>Volatile.Write</c>; readers load the index with <c>Volatile.Read</c>. So a reader never sees
/// an index whose palette slot is unset.</para>
///
/// <para><b>4. Palette entries are append-only.</b> A slot, once written, is never changed or
/// removed, and an id is appended only when the palette does not hold it yet, so a palette never
/// holds duplicates. Entries whose cells were all overwritten stay (stale) until the storage is
/// rebuilt by <see cref="FromDense"/>.</para>
///
/// <para><b>5. An upgrade publishes a new object.</b> A write that does not fit the mode (a second
/// id into Uniform, a 257th id into Indexed) returns a new object holding every cell plus the new
/// one; the owner publishes it with one volatile reference swap. A reader still holding the old
/// object misses only that one write. Upgrades go one way: Uniform, Indexed, Direct.</para>
///
/// <para><b>6. Uniform objects are immutable</b> and may be shared between chunks and snapshots
/// (<see cref="Air"/> is every fresh chunk's storage); <see cref="Set"/> never mutates one.</para>
///
/// <para><b>7. Compaction happens only in <see cref="FromDense"/>.</b> It picks the smallest mode
/// for the cells and a duplicate-free palette of exactly the ids present; nothing else ever
/// shrinks a storage.</para>
/// </summary>
internal sealed class ChunkBlocks
{
    /// <summary>The most distinct ids an Indexed storage holds (one index byte per cell).</summary>
    internal const int PaletteCapacity = 256;

    internal enum StorageMode
    {
        Uniform,
        Indexed,
        Direct,
    }

    /// <summary>The shared all-air storage every fresh chunk starts from.</summary>
    internal static readonly ChunkBlocks Air = new(0);

    private readonly ushort _uniform;
    private readonly ushort[]? _palette;
    private int _paletteCount;
    private readonly byte[]? _indices;
    private readonly ushort[]? _direct;
    private bool _compact;

    // FromDense's reverse lookup, id -> slot + 1 (0 = not in the palette yet). Sized for the whole
    // ushort range rather than from content, and zeroed again by every call before it returns.
    [ThreadStatic] private static ushort[]? t_slotOfId;

    [ThreadStatic] private static ushort[]? t_dense;

    private ChunkBlocks(ushort uniform)
    {
        _uniform = uniform;
        _compact = true;
    }

    private ChunkBlocks(ushort[] palette, int count, byte[] indices, bool compact)
    {
        // Get and CopyRun index the palette with a byte unchecked, so it must hold all 256 slots.
        if (palette.Length != PaletteCapacity)
            throw new ArgumentException($"A palette holds {PaletteCapacity} slots, not {palette.Length}.", nameof(palette));
        _palette = palette;
        _paletteCount = count;
        _indices = indices;
        _compact = compact;
    }

    private ChunkBlocks(ushort[] direct, bool compact)
    {
        _direct = direct;
        _compact = compact;
    }

    /// <summary>A Uniform storage of <paramref name="id"/> (<see cref="Air"/> for 0).</summary>
    internal static ChunkBlocks Uniform(ushort id) => id == 0 ? Air : new ChunkBlocks(id);

    internal StorageMode Mode =>
        _direct != null ? StorageMode.Direct : _indices != null ? StorageMode.Indexed : StorageMode.Uniform;

    /// <summary>
    /// True when this storage was built by <see cref="FromDense"/> and no cell has changed since
    /// (always true for Uniform): its palette holds exactly the ids present and its mode is the
    /// smallest that fits, so a restore can clone it instead of recompacting. Only meaningful on an
    /// object nobody is writing, such as a snapshot's; never read it on a live chunk from another
    /// thread.
    /// </summary>
    internal bool IsCompact => _compact;

    /// <summary>
    /// The calling thread's dense work array of <see cref="Chunk.BlockCount"/> ids. Its callers are
    /// exactly the worldgen fill (<c>ChunkTerrainFill.GenerateBlockData</c>), <c>ChunkRestore</c>,
    /// <c>ChunkSaveCodec.ReadCore</c> and <c>ChunkNetCodec.Decode</c>. Its contents are valid only
    /// inside one such operation; it is not reentrant, and no caller may invoke foreign code (a
    /// callback) between filling it and its last read.
    /// </summary>
    internal static ushort[] DenseScratch => t_dense ??= new ushort[Chunk.BlockCount];

    /// <summary>The id of cell <paramref name="i"/> (Idx order).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ushort Get(int i)
    {
        var indices = _indices;
        if (indices != null)
            return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_palette!), Volatile.Read(ref indices[i]));
        var direct = _direct;
        return direct != null ? direct[i] : _uniform;
    }

    /// <summary>
    /// Writes <paramref name="id"/> into cell <paramref name="i"/> and reports the id it replaced.
    /// Returns this object when the write fit the mode (or changed nothing), or a new, upgraded
    /// object the owner must publish in its place (invariant 5). Allocates only on an upgrade.
    /// </summary>
    internal ChunkBlocks Set(int i, ushort id, out ushort previous)
    {
        var indices = _indices;
        if (indices != null)
            return SetIndexed(indices, i, id, out previous);

        var direct = _direct;
        if (direct != null)
        {
            previous = direct[i];
            if (previous == id)
                return this;
            if (_compact)
                _compact = false;
            direct[i] = id;
            return this;
        }

        previous = _uniform;
        if (previous == id)
            return this;
        var palette = new ushort[PaletteCapacity];
        palette[0] = _uniform;
        palette[1] = id;
        var upgraded = new byte[Chunk.BlockCount];
        upgraded[i] = 1;
        return new ChunkBlocks(palette, 2, upgraded, compact: false);
    }

    private ChunkBlocks SetIndexed(byte[] indices, int i, ushort id, out ushort previous)
    {
        var palette = _palette!;
        previous = palette[indices[i]];
        if (previous == id)
            return this;
        if (_compact)
            _compact = false;

        var count = _paletteCount;
        var slot = palette.AsSpan(0, count).IndexOf(id);
        if (slot < 0)
        {
            if (count == PaletteCapacity)
            {
                var dense = ToDense();
                dense[i] = id;
                return new ChunkBlocks(dense, compact: false);
            }

            // Invariant 3: the slot is filled and counted before any index can name it.
            palette[count] = id;
            Volatile.Write(ref _paletteCount, count + 1);
            slot = count;
        }

        Volatile.Write(ref indices[i], (byte)slot);
        return this;
    }

    /// <summary>
    /// Copies cells <paramref name="start"/> .. <paramref name="start"/> + <paramref name="length"/>
    /// (Idx order) into the front of <paramref name="destination"/>.
    /// </summary>
    internal void CopyRun(int start, int length, Span<ushort> destination)
    {
        var indices = _indices;
        if (indices != null)
        {
            ref var palette = ref MemoryMarshal.GetArrayDataReference(_palette!);
            var source = indices.AsSpan(start, length);
            var target = destination[..source.Length];
            for (var k = 0; k < source.Length; k++)
                target[k] = Unsafe.Add(ref palette, Volatile.Read(ref source[k]));
            return;
        }

        var direct = _direct;
        if (direct != null)
        {
            direct.AsSpan(start, length).CopyTo(destination);
            return;
        }

        destination[..length].Fill(_uniform);
    }

    /// <summary>A new dense array of every cell (Idx order).</summary>
    internal ushort[] ToDense()
    {
        var dense = new ushort[Chunk.BlockCount];
        CopyRun(0, Chunk.BlockCount, dense);
        return dense;
    }

    /// <summary>
    /// The ids this storage may hold: the one id (Uniform), the palette (Indexed) or every cell
    /// (Direct). Unless <see cref="IsCompact"/>, an Indexed palette is a superset that can keep
    /// stale entries, so use it only for conservative questions (could this chunk hold X? are all
    /// its ids in range?).
    /// </summary>
    internal ReadOnlySpan<ushort> IdSpan
    {
        get
        {
            if (_indices != null)
                return _palette!.AsSpan(0, Volatile.Read(ref _paletteCount));
            var direct = _direct;
            if (direct != null)
                return direct;
            return MemoryMarshal.CreateReadOnlySpan(in _uniform, 1);
        }
    }

    /// <summary>The per-cell palette indices of an Indexed storage (Idx order), for the net encoder.</summary>
    internal ReadOnlySpan<byte> IndexSpan =>
        _indices ?? throw new InvalidOperationException($"IndexSpan needs an Indexed storage, not {Mode}.");

    /// <summary>The number of non-air (non-zero) cells.</summary>
    internal int CountNonAir()
    {
        var indices = _indices;
        if (indices != null)
        {
            var airSlot = IdSpan.IndexOf((ushort)0);
            return airSlot < 0 ? Chunk.BlockCount : Chunk.BlockCount - indices.AsSpan().Count((byte)airSlot);
        }

        var direct = _direct;
        if (direct != null)
            return Chunk.BlockCount - direct.AsSpan().Count((ushort)0);
        return _uniform == 0 ? 0 : Chunk.BlockCount;
    }

    /// <summary>
    /// An independent copy (the same object for Uniform, invariant 6). Carries <see cref="IsCompact"/>.
    /// </summary>
    internal ChunkBlocks Clone()
    {
        var indices = _indices;
        if (indices != null)
        {
            return new ChunkBlocks((ushort[])_palette!.Clone(), Volatile.Read(ref _paletteCount),
                (byte[])indices.Clone(), _compact);
        }

        var direct = _direct;
        return direct != null ? new ChunkBlocks((ushort[])direct.Clone(), _compact) : this;
    }

    /// <summary>
    /// Builds the smallest storage that holds <paramref name="dense"/> (<see cref="Chunk.BlockCount"/>
    /// ids in Idx order): Uniform for one distinct id, Indexed with a duplicate-free palette in
    /// first-seen order for up to <see cref="PaletteCapacity"/>, Direct past that. The result is
    /// compact and shares no array with <paramref name="dense"/>. The only place storage is compacted
    /// (invariant 7).
    /// </summary>
    internal static ChunkBlocks FromDense(ReadOnlySpan<ushort> dense)
    {
        if (dense.Length != Chunk.BlockCount)
            throw new ArgumentException($"A chunk holds {Chunk.BlockCount} cells, not {dense.Length}.", nameof(dense));
        var first = dense[0];
        if (dense.IndexOfAnyExcept(first) < 0)
            return Uniform(first);

        var slotOfId = t_slotOfId ??= new ushort[ushort.MaxValue + 1];
        var palette = new ushort[PaletteCapacity];
        var indices = new byte[Chunk.BlockCount];
        var count = 0;
        try
        {
            // Runs of one id are common (columns of stone, air), so the previous cell's slot is reused
            // without touching the lookup table.
            var runId = -1;
            byte runSlot = 0;
            for (var i = 0; i < dense.Length; i++)
            {
                var id = dense[i];
                if (id != runId)
                {
                    var slot = slotOfId[id];
                    if (slot == 0)
                    {
                        if (count == PaletteCapacity)
                            return new ChunkBlocks(dense.ToArray(), compact: true);
                        palette[count] = id;
                        slot = (ushort)++count;
                        slotOfId[id] = slot;
                    }

                    runId = id;
                    runSlot = (byte)(slot - 1);
                }

                indices[i] = runSlot;
            }
        }
        finally
        {
            for (var k = 0; k < count; k++)
                slotOfId[palette[k]] = 0;
        }

        return new ChunkBlocks(palette, count, indices, compact: true);
    }
}
