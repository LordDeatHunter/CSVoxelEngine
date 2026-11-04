using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.Lod;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Saves;

/// <summary>
/// One world's far-LOD edit state: the removal caps (<see cref="LodColumnCaps.LowerColumnBelow(int, int, int)"/>),
/// the surface overrides (<see cref="LodSurface"/>), the felled-canopy markers
/// (<see cref="TreeGenerator.PoisonCanopy"/>) and the crowns a reactive growth added back
/// (<see cref="TreeGenerator.SetCanopyAdded"/>). All four are process-global tables that only
/// record edits, so without this a restart would redraw the far terrain as freshly generated and
/// a dug-out or built-up area would snap back at range. <see cref="Capture"/> copies the live
/// tables, <see cref="Import"/> merges a saved copy back in; it must run BEFORE
/// <c>LodManager.Start</c> so the first region builds see it (regions built later pick it up on
/// their own).
///
/// Layout of <see cref="FileName"/>, all little-endian:
/// <code>
///   "CVXL"            4 ASCII bytes of magic
///   version           byte, currently 2
///   Deflate body:
///     int n, then n x (int cx, int cz, 256 x int cap)        caps, int.MaxValue = uncapped
///     byte p, then p x BinaryWriter string                   block palette (overrides + added crowns)
///     int n, then n x (int cx, int cz, 256 x int override)   int.MinValue = none, else
///                                                            top &lt;&lt; 8 | palette index (0 = generated tile)
///     int n, then n x (int x, int z)                         felled-canopy columns
///     int n, then n x (int cx, int cz, 256 x int crown)      version 2 only: grown crowns added back,
///                                                            int.MinValue = none, else
///                                                            top &lt;&lt; 8 | palette index (the leaf block)
/// </code>
/// Palette blocks travel as NAMES for the same reason chunk files do (<see cref="ChunkSaveCodec"/>):
/// block ids are only stable within one run. A name this run no longer knows reads back as 0,
/// the generated tile rule for an override (keeping the saved height) and no leaf for a crown.
/// </summary>
public sealed class LodEditState(
    IReadOnlyList<(int CX, int CZ, int[] Values)> caps,
    IReadOnlyList<(int CX, int CZ, int[] Values)> overrides,
    IReadOnlyList<(int X, int Z)> disturbed,
    IReadOnlyList<(int CX, int CZ, int[] Values)>? canopyAdded = null)
{
    /// <summary>The file the state is saved to, in the world folder.</summary>
    public const string FileName = "lod-edits.bin";

    /// <summary>The format version this build writes; it also reads <see cref="FirstVersion"/>, which has no crown section.</summary>
    public const byte Version = 2;

    /// <summary>The oldest format version this build still reads.</summary>
    public const byte FirstVersion = 1;

    private static readonly byte[] Magic = "CVXL"u8.ToArray();

    private const int BucketSize = Chunk.Size * Chunk.Size;

    // Bounds on a section's entry count, far past anything a session writes; a larger count means
    // a corrupt file, and refusing it keeps a flipped bit from allocating gigabytes.
    private const int MaxBuckets = 1 << 20;
    private const int MaxDisturbed = 1 << 24;

    /// <summary>Per chunk column, the 256 caps indexed like a heightmap.</summary>
    public IReadOnlyList<(int CX, int CZ, int[] Values)> Caps { get; } = caps;

    /// <summary>Per chunk column, the 256 packed overrides (<see cref="LodSurface.Pack"/>, this run's block ids).</summary>
    public IReadOnlyList<(int CX, int CZ, int[] Values)> Overrides { get; } = overrides;

    /// <summary>Every felled-canopy column.</summary>
    public IReadOnlyList<(int X, int Z)> Disturbed { get; } = disturbed;

    /// <summary>Per chunk column, the 256 packed grown crowns (<see cref="LodSurface.Pack"/> of the top and the leaf block, this run's ids).</summary>
    public IReadOnlyList<(int CX, int CZ, int[] Values)> CanopyAdded { get; } = canopyAdded ?? [];

    /// <summary>
    /// The sum of the four tables' edit counters. Any cap, override or canopy edit changes it, so
    /// an autosave can skip rewriting the file while it stands still.
    /// </summary>
    public static int CombinedVersion =>
        unchecked(LodColumnCaps.CapEditVersion + LodSurface.EditVersion + TreeCanopy.CanopyEditVersion
                  + TreeCanopy.CanopyAddedVersion);

    /// <summary>Copies the live tables. Main thread, the tables' only writer.</summary>
    public static LodEditState Capture() =>
        new(LodColumnCaps.ExportColumnCaps(), LodSurface.Export(), TreeCanopy.ExportCanopyDisturbed(),
            TreeCanopy.ExportCanopyAdded());

    /// <summary>
    /// Merges this state into the live tables: the lower cap wins, a live override or grown crown
    /// wins over the saved one, and the canopy markers are unioned, so an import never undoes an
    /// edit made first.
    /// </summary>
    public void Import()
    {
        LodColumnCaps.ImportColumnCaps(Caps);
        LodSurface.Import(Overrides);
        TreeCanopy.ImportCanopyDisturbed(Disturbed);
        TreeCanopy.ImportCanopyAdded(CanopyAdded);
    }

    /// <summary>Encodes with this run's block names.</summary>
    public void Write(Stream output) => Write(output, static id => BlockType.ById[id]?.Name);

    /// <summary>
    /// Encodes into <paramref name="output"/> (left open). A null name from
    /// <paramref name="nameOf"/> is written as "" and reads back as the generated tile rule.
    /// </summary>
    public void Write(Stream output, Func<byte, string?> nameOf)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(nameOf);

        // Block id -> 1-based palette index, in first-seen order; 0 stays "generated tile".
        Span<byte> paletteIndexOf = stackalloc byte[256];
        paletteIndexOf.Clear();
        var palette = new List<byte>();
        foreach (var (_, _, values) in Overrides.Concat(CanopyAdded))
        foreach (var value in values)
        {
            if (value == LodSurface.None) continue;
            LodSurface.Unpack(value, out _, out var block);
            if (block == 0 || paletteIndexOf[block] != 0) continue;
            palette.Add(block);
            paletteIndexOf[block] = (byte)palette.Count;
        }

        output.Write(Magic);
        output.WriteByte(Version);

        using var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true);
        using var writer = new BinaryWriter(deflate, Encoding.UTF8, leaveOpen: true);

        WriteBuckets(writer, Caps);

        writer.Write((byte)palette.Count);
        foreach (var id in palette) writer.Write(nameOf(id) ?? "");

        WritePackedBuckets(writer, Overrides, paletteIndexOf);

        writer.Write(Disturbed.Count);
        foreach (var (x, z) in Disturbed)
        {
            writer.Write(x);
            writer.Write(z);
        }

        WritePackedBuckets(writer, CanopyAdded, paletteIndexOf);
    }

    // Buckets of packed (top, block) values with each block replaced by its 1-based palette index
    private static void WritePackedBuckets(BinaryWriter writer, IReadOnlyList<(int CX, int CZ, int[] Values)> buckets,
        ReadOnlySpan<byte> paletteIndexOf)
    {
        writer.Write(buckets.Count);
        foreach (var (cx, cz, values) in buckets)
        {
            RequireBucketSize(cx, cz, values);
            writer.Write(cx);
            writer.Write(cz);
            foreach (var raw in values)
            {
                var value = raw;
                if (value != LodSurface.None)
                {
                    LodSurface.Unpack(value, out var top, out var block);
                    value = LodSurface.Pack(top, paletteIndexOf[block]);
                }

                writer.Write(value);
            }
        }
    }

    /// <summary>Decodes with this run's block ids.</summary>
    public static LodEditState Read(Stream input) =>
        Read(input, static name => ContentRegistry.TryResolveBlock(name, out var id) ? id : null);

    /// <summary>
    /// Decodes a state written by <see cref="Write(Stream, Func{byte, string?})"/>. A bad magic, an
    /// unsupported version, an absurd count or a truncated body all throw
    /// <see cref="InvalidDataException"/>; an unknown or empty block name maps to 0 (the generated
    /// tile rule) with one warning per read.
    /// </summary>
    public static LodEditState Read(Stream input, Func<string, byte?> idOf)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(idOf);

        try
        {
            Span<byte> header = stackalloc byte[5];
            input.ReadExactly(header);
            if (!header[..4].SequenceEqual(Magic))
                throw new InvalidDataException("not a LOD edit file (bad magic)");
            var version = header[4];
            if (version is < FirstVersion or > Version)
                throw new InvalidDataException($"unsupported LOD edit file version {version}");

            using var deflate = new DeflateStream(input, CompressionMode.Decompress, leaveOpen: true);
            using var reader = new BinaryReader(deflate, Encoding.UTF8, leaveOpen: true);

            var caps = ReadBuckets(reader);

            var paletteCount = reader.ReadByte();
            var palette = new byte[paletteCount + 1];   // palette[0] = generated tile
            List<string>? unknown = null;
            for (var k = 1; k <= paletteCount; k++)
            {
                var name = reader.ReadString();
                var id = name.Length > 0 ? idOf(name) : null;
                if (id is { } resolved)
                    palette[k] = resolved;
                else
                    (unknown ??= []).Add(name.Length > 0 ? name : "<empty>");
            }

            var overrides = ReadPackedBuckets(reader, palette);

            var disturbedCount = reader.ReadInt32();
            if (disturbedCount < 0 || disturbedCount > MaxDisturbed)
                throw new InvalidDataException($"bad canopy marker count {disturbedCount}");
            var disturbed = new (int X, int Z)[disturbedCount];
            for (var i = 0; i < disturbedCount; i++)
                disturbed[i] = (reader.ReadInt32(), reader.ReadInt32());

            var canopyAdded = version >= 2 ? ReadPackedBuckets(reader, palette) : [];

            if (unknown != null)
                Log.Warn($"LOD edits: unknown override blocks {string.Join(", ", unknown)} fall back to the generated tile");

            return new LodEditState(caps, overrides, disturbed, canopyAdded);
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException("truncated LOD edit file", e);
        }
    }

    /// <summary>The encoded file contents.</summary>
    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        Write(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Decodes saved file contents: null in (no file yet) gives null, and so does a corrupt file,
    /// with a warning, so a damaged save only costs the far LOD its edits, never the world load.
    /// </summary>
    public static LodEditState? TryParse(byte[]? bytes)
    {
        if (bytes == null) return null;
        try
        {
            return Read(new MemoryStream(bytes, writable: false));
        }
        catch (InvalidDataException e)
        {
            Log.Warn($"LOD edits: ignoring {FileName}: {e.Message}");
            return null;
        }
    }

    private static void WriteBuckets(BinaryWriter writer, IReadOnlyList<(int CX, int CZ, int[] Values)> buckets)
    {
        writer.Write(buckets.Count);
        foreach (var (cx, cz, values) in buckets)
        {
            RequireBucketSize(cx, cz, values);
            writer.Write(cx);
            writer.Write(cz);
            foreach (var value in values) writer.Write(value);
        }
    }

    private static void RequireBucketSize(int cx, int cz, int[] values)
    {
        if (values.Length != BucketSize)
            throw new ArgumentException($"bucket ({cx}, {cz}) holds {values.Length} values, not {BucketSize}");
    }

    // Buckets of packed (top, palette index) values, the index mapped back to this run's block id
    // (palette[0] = the generated tile / no leaf)
    private static List<(int CX, int CZ, int[] Values)> ReadPackedBuckets(BinaryReader reader, byte[] palette)
    {
        var buckets = ReadBuckets(reader);
        foreach (var (_, _, values) in buckets)
        {
            for (var i = 0; i < values.Length; i++)
            {
                if (values[i] == LodSurface.None) continue;
                LodSurface.Unpack(values[i], out var top, out var index);
                if (index >= palette.Length)
                    throw new InvalidDataException($"palette index {index} past {palette.Length - 1}");
                values[i] = LodSurface.Pack(top, palette[index]);
            }
        }

        return buckets;
    }

    private static List<(int CX, int CZ, int[] Values)> ReadBuckets(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > MaxBuckets)
            throw new InvalidDataException($"bad bucket count {count}");

        var buckets = new List<(int CX, int CZ, int[] Values)>(Math.Min(count, 4096));
        for (var b = 0; b < count; b++)
        {
            var cx = reader.ReadInt32();
            var cz = reader.ReadInt32();
            var values = new int[BucketSize];
            for (var i = 0; i < BucketSize; i++) values[i] = reader.ReadInt32();
            buckets.Add((cx, cz, values));
        }

        return buckets;
    }
}
