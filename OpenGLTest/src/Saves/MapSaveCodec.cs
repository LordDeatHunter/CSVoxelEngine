using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using VoxelEngine.Map;

namespace VoxelEngine.Saves;

/// <summary>
/// Reads and writes a world's <see cref="FileName"/>: the explored map (<see cref="MapTracker"/>'s surface and sky
/// tiles; a cave slice follows the eye and is never kept). GL-free and content-free: a tile is its colors and heights,
/// keyed by its layer kind and chunk column.
/// <code>
///   "CVMP", byte version, then a Deflate body of
///     int n, then n x (byte layer kind, int chunk x, int chunk z, 256 x uint texel, 256 x short height)
/// </code>
/// A missing, truncated or corrupt file reads as an unexplored map, with a warning for a damaged one.
/// </summary>
public static class MapSaveCodec
{
    public const string FileName = "map.bin";

    public const byte Version = 1;

    private static readonly byte[] Magic = "CVMP"u8.ToArray();

    public static byte[] ToBytes(IReadOnlyList<(MapLayerKind Kind, MapTile Tile)> tiles)
    {
        using var output = new MemoryStream();
        output.Write(Magic);
        output.WriteByte(Version);
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new BinaryWriter(deflate))
        {
            writer.Write(tiles.Count);
            foreach (var (kind, tile) in tiles)
            {
                writer.Write((byte)kind);
                writer.Write(tile.ChunkX);
                writer.Write(tile.ChunkZ);
                writer.Write(MemoryMarshal.AsBytes(tile.Texels.AsSpan()));
                writer.Write(MemoryMarshal.AsBytes(tile.Heights.AsSpan()));
            }
        }

        return output.ToArray();
    }

    /// <summary>The saved tiles, or null for no file or a file that is not this format (warned).</summary>
    public static IReadOnlyList<(MapLayerKind Kind, MapTile Tile)>? TryParse(byte[]? bytes)
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
            if (count is < 0 or > MapConfig.MaxTiles) throw new InvalidDataException("bad tile count");

            var tiles = new List<(MapLayerKind, MapTile)>(count);
            for (var i = 0; i < count; i++)
            {
                var kind = (MapLayerKind)reader.ReadByte();
                if (kind is not (MapLayerKind.Surface or MapLayerKind.Sky)) throw new InvalidDataException("bad layer");
                var tile = new MapTile(reader.ReadInt32(), reader.ReadInt32());
                reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(tile.Texels.AsSpan()));
                reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(tile.Heights.AsSpan()));
                tiles.Add((kind, tile));
            }

            return tiles;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException)
        {
            Log.Warn($"Map: ignoring {FileName}: {e.Message}");
            return null;
        }
    }
}
