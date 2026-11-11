using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using VoxelEngine.Chunks;
using VoxelEngine.Content;

namespace VoxelEngine.Saves;

/// <summary>
/// The on-disk format of one saved chunk (<see cref="ChunkSnapshot"/>). Block ids are only
/// stable within one run (they are handed out by file name at startup, see
/// <see cref="ContentRegistry"/>), so the file stores a palette of block NAMES and the body
/// indexes it; loading maps each name back to this run's id, and a block that no longer exists
/// becomes air with one warning per file instead of failing the load.
///
/// Layout, all little-endian:
/// <code>
///   "CVXC"            4 ASCII bytes of magic
///   version           byte, currently 3 (3: ushort palette count and wide indices for ids past 255;
///                     2: tree wood and leaves carry their graph state; older files regenerate)
///   flags             byte: bit 0 = a state array follows, bit 1 = player-placed cells follow
///   paletteCount      ushort: distinct non-air ids, in first-seen (Idx) order
///   paletteCount x    BinaryWriter string (7-bit length prefix + UTF-8); "" = unknown block
///   Deflate body:
///     4096 indices    palette index per cell (0 = air, k = palette[k - 1]): one byte each when
///                     paletteCount &lt;= 255, else one ushort each
///     4096 bytes      state bytes, only when flag bit 0 is set
///     ushort + n x ushort   player-placed Idx indices, only when flag bit 1 is set
/// </code>
/// GL-free and content-free: the id/name mapping is passed in, so the tests run without content
/// and the writer thread never touches the registry's mutable state.
/// </summary>
public static class ChunkSaveCodec
{
    private static readonly byte[] Magic = "CVXC"u8.ToArray();

    /// <summary>The format version this build writes and the only one it reads.</summary>
    public const byte Version = 3;

    private const byte HasStateFlag = 1;
    private const byte HasPlacedFlag = 2;

    // id -> 1-based palette index while one Write runs (0 = air or not seen yet). One table per
    // thread, sized for every id; each Write zeroes only the entries its palette set.
    [ThreadStatic] private static ushort[]? _indexOfId;

    /// <summary>
    /// Encodes <paramref name="snapshot"/> into <paramref name="output"/> (left open). A null
    /// name from <paramref name="nameOf"/> is written as "" and reads back as air: the save
    /// writer runs on its own thread and must never throw over a content problem.
    /// </summary>
    public static void Write(Stream output, ChunkSnapshot snapshot, Func<ushort, string?> nameOf)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(nameOf);

        // The cells, decoded into the array that becomes the body's indices. Not the thread's
        // ChunkBlocks.DenseScratch: nameOf is the caller's code and runs mid-write. The name palette
        // is re-derived from the cells (never the storage's in-memory palette), so the file depends
        // only on the cells and an edited chunk's stale palette entries never reach it
        var indices = new ushort[Chunk.BlockCount];
        snapshot.Storage.CopyRun(0, Chunk.BlockCount, indices);
        var indexOfId = _indexOfId ??= new ushort[ushort.MaxValue + 1];
        var palette = new List<ushort>();
        try
        {
            // Filled in first-seen order; a chunk holds at most BlockCount distinct ids, so a
            // palette index always fits a ushort
            foreach (var id in indices)
            {
                if (id == BlockType.Air || indexOfId[id] != 0) continue;
                palette.Add(id);
                indexOfId[id] = (ushort)palette.Count;
            }

            var flags = (byte)((snapshot.State != null ? HasStateFlag : 0)
                               | (snapshot.PlayerPlaced != null ? HasPlacedFlag : 0));

            using (var header = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true))
            {
                header.Write(Magic);
                header.Write(Version);
                header.Write(flags);
                header.Write((ushort)palette.Count);
                foreach (var id in palette) header.Write(nameOf(id) ?? "");
            }

            for (var i = 0; i < indices.Length; i++) indices[i] = indexOfId[indices[i]];
            WriteBody(output, snapshot, indices, palette.Count);
        }
        finally
        {
            // Walk the palette, never the whole table: the table is sized for every id
            foreach (var id in palette) indexOfId[id] = 0;
        }
    }

    // The Deflate body: the cell indices at the palette's width, then the state and the placed cells
    private static void WriteBody(Stream output, ChunkSnapshot snapshot, ushort[] indices, int paletteCount)
    {
        using var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true);
        if (paletteCount <= byte.MaxValue)
        {
            var narrow = new byte[indices.Length];
            for (var i = 0; i < narrow.Length; i++) narrow[i] = (byte)indices[i];
            deflate.Write(narrow);
        }
        else
        {
            if (!BitConverter.IsLittleEndian) BinaryPrimitives.ReverseEndianness(indices, indices);
            deflate.Write(MemoryMarshal.AsBytes(indices.AsSpan()));
        }

        if (snapshot.State is { } state) deflate.Write(state);

        if (snapshot.PlayerPlaced is { } placed)
        {
            using var body = new BinaryWriter(deflate, Encoding.UTF8, leaveOpen: true);
            body.Write((ushort)placed.Length);
            foreach (var index in placed) body.Write(index);
        }
    }

    /// <summary>
    /// Decodes a chunk written by <see cref="Write"/>. Each palette name goes through
    /// <paramref name="idOf"/>; a name it does not know (or the "" of an unknown block) decodes
    /// to air, and the set of such names is reported once per call with <see cref="Log.Warn"/>.
    /// Bad magic, an unknown version, a truncated file or an out-of-range index all surface as
    /// one exception type, <see cref="InvalidDataException"/>, so callers catch that (plus
    /// <see cref="IOException"/> for the disk itself).
    /// </summary>
    public static ChunkSnapshot Read(Stream input, Func<string, ushort?> idOf)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(idOf);

        try
        {
            return ReadCore(input, idOf);
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException("The chunk save ends early.", e);
        }
        catch (FormatException e)
        {
            throw new InvalidDataException("The chunk save's palette is malformed.", e);
        }
    }

    private static ChunkSnapshot ReadCore(Stream input, Func<string, ushort?> idOf)
    {
        using var header = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);

        Span<byte> magic = stackalloc byte[4];
        header.BaseStream.ReadExactly(magic);
        if (!magic.SequenceEqual(Magic))
            throw new InvalidDataException("Not a chunk save (bad magic).");

        var version = header.ReadByte();
        if (version != Version)
            throw new InvalidDataException($"Unsupported chunk save version {version} (this build reads {Version}).");

        var flags = header.ReadByte();
        var paletteCount = header.ReadUInt16();

        // Palette index -> this run's id; index 0 is air
        var idOfIndex = new ushort[paletteCount + 1];
        HashSet<string>? unknown = null;
        for (var k = 1; k <= paletteCount; k++)
        {
            var name = header.ReadString();
            var id = name.Length == 0 ? null : idOf(name);
            if (id is { } resolved)
            {
                idOfIndex[k] = resolved;
            }
            else
            {
                unknown ??= new HashSet<string>(StringComparer.Ordinal);
                unknown.Add(name.Length == 0 ? "<unnamed>" : name);
            }
        }

        using var deflate = new DeflateStream(input, CompressionMode.Decompress, leaveOpen: true);

        // The cells go through the thread's dense scratch and are compacted right after the loop,
        // before anything else runs (no callback is invoked while the scratch holds them); names
        // that resolved to one id, and every unknown one, merge into one palette entry there
        var indices = ReadIndices(deflate, paletteCount);
        var dense = ChunkBlocks.DenseScratch;
        for (var i = 0; i < dense.Length; i++)
        {
            var index = indices[i];
            if (index > paletteCount)
                throw new InvalidDataException($"Chunk save cell {i} names palette entry {index} of {paletteCount}.");
            dense[i] = idOfIndex[index];
        }

        var storage = ChunkBlocks.FromDense(dense);

        byte[]? state = null;
        if ((flags & HasStateFlag) != 0)
        {
            state = new byte[Chunk.BlockCount];
            deflate.ReadExactly(state);
        }

        ushort[]? placed = null;
        if ((flags & HasPlacedFlag) != 0)
        {
            using var body = new BinaryReader(deflate, Encoding.UTF8, leaveOpen: true);
            var count = body.ReadUInt16();
            if (count > Chunk.BlockCount)
                throw new InvalidDataException($"Chunk save lists {count} placed cells, more than a chunk holds.");
            placed = new ushort[count];
            for (var i = 0; i < count; i++)
            {
                var index = body.ReadUInt16();
                if (index >= Chunk.BlockCount)
                    throw new InvalidDataException($"Chunk save lists placed cell {index}, outside the chunk.");
                placed[i] = index;
            }
        }

        if (unknown != null)
            Log.Warn($"Chunk save names unknown block(s) {string.Join(", ", unknown)}; loaded as air.");

        // The storage, state and placed arrays are this call's own, so the snapshot takes them over
        return new ChunkSnapshot(storage, state, placed is { Length: > 0 } ? placed : null);
    }

    // The cell indices at the width WriteBody chose for this palette, widened to ushort
    private static ushort[] ReadIndices(Stream deflate, int paletteCount)
    {
        var indices = new ushort[Chunk.BlockCount];
        if (paletteCount <= byte.MaxValue)
        {
            var narrow = new byte[indices.Length];
            deflate.ReadExactly(narrow);
            for (var i = 0; i < narrow.Length; i++) indices[i] = narrow[i];
            return indices;
        }

        deflate.ReadExactly(MemoryMarshal.AsBytes(indices.AsSpan()));
        if (!BitConverter.IsLittleEndian) BinaryPrimitives.ReverseEndianness(indices, indices);
        return indices;
    }
}
