using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using VoxelEngine.Chunks;
using VoxelEngine.Chunks.Meshing;

namespace VoxelEngine.Saves;

/// <summary>
/// Reads and writes a world's <see cref="FileName"/>: the permanent chop damage of its natural trunk cells
/// (<see cref="WoodDamage"/>). GL-free and content-free (a cell is saved by its world position, so no block id
/// is stored; whether the cell still holds natural wood is judged when it is drawn).
/// <code>
///   "CVWD", byte version, then a Deflate body of
///     int n, then n x (int x, int y, int z, float damage, byte face)
/// </code>
/// A missing, truncated or corrupt file reads as no damage, with a warning for a damaged one.
/// </summary>
public static class WoodDamageCodec
{
    public const string FileName = "wood-damage.bin";

    public const byte Version = 1;

    private static readonly byte[] Magic = "CVWD"u8.ToArray();

    // Far past anything a session chops; a larger count means a corrupt file
    private const int MaxEntries = 1 << 22;

    public static byte[] ToBytes(IReadOnlyList<(long Key, WoodDamage.Entry Entry)> entries)
    {
        using var output = new MemoryStream();
        output.Write(Magic);
        output.WriteByte(Version);
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new BinaryWriter(deflate))
        {
            writer.Write(entries.Count);
            foreach (var (key, entry) in entries)
            {
                CellKey.Unpack(key, out var x, out var y, out var z);
                writer.Write(x);
                writer.Write(y);
                writer.Write(z);
                writer.Write(entry.Damage);
                writer.Write((byte)entry.Face);
            }
        }

        return output.ToArray();
    }

    /// <summary>The saved entries, or null for no file or a file that is not this format (warned).</summary>
    public static IReadOnlyList<(long Key, WoodDamage.Entry Entry)>? TryParse(byte[]? bytes)
    {
        if (bytes == null) return null;
        try
        {
            using var input = new MemoryStream(bytes);
            Span<byte> head = stackalloc byte[5];
            input.ReadExactly(head);
            if (!head[..4].SequenceEqual(Magic) || head[4] != Version)
                throw new InvalidDataException("unknown header");

            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var reader = new BinaryReader(deflate);
            var count = reader.ReadInt32();
            if (count is < 0 or > MaxEntries) throw new InvalidDataException("bad entry count");

            var entries = new List<(long, WoodDamage.Entry)>(count);
            for (var i = 0; i < count; i++)
            {
                var x = reader.ReadInt32();
                var y = reader.ReadInt32();
                var z = reader.ReadInt32();
                var damage = reader.ReadSingle();
                var face = reader.ReadByte();
                if (!float.IsFinite(damage) || damage <= 0f || face > WoodDamage.NoFace)
                    throw new InvalidDataException("bad entry");
                entries.Add((CellKey.Pack(x, y, z), new WoodDamage.Entry(damage, face)));
            }

            return entries;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException)
        {
            Log.Warn($"Wood damage: ignoring {FileName}: {e.Message}");
            return null;
        }
    }
}
