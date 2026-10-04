using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace silkgltriangle;

/// <summary>
/// Wire form of one far-LOD region's mesh (<see cref="LodMeshData"/>): the level and region
/// coordinates, the vertical bounds, the opaque and water vertex/index lists and the per-column
/// chunk-Y ranges. <see cref="Vertex"/> is a blittable 32-byte struct, so its list travels as raw
/// bytes; the whole payload is Deflate-compressed. This is how a client gets far terrain it could
/// not generate (a custom or premade server world). GL-free and content-free.
/// </summary>
public static class LodNetCodec
{
    public static byte[] Encode(LodMeshData data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new BinaryWriter(deflate, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(data.Level);
            writer.Write(data.X);
            writer.Write(data.Z);
            writer.Write(data.YMin);
            writer.Write(data.YMax);
            WriteVertices(writer, data.Vertices);
            WriteIndices(writer, data.Indices);
            WriteVertices(writer, data.WaterVertices);
            WriteIndices(writer, data.WaterIndices);
            WriteInts(writer, data.ColumnCyMin);
            WriteInts(writer, data.ColumnCyMax);
        }

        return output.ToArray();
    }

    public static LodMeshData Decode(byte[] payload)
    {
        using var input = new MemoryStream(payload, writable: false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var reader = new BinaryReader(deflate, System.Text.Encoding.UTF8, leaveOpen: true);

        var level = reader.ReadInt32();
        var x = reader.ReadInt32();
        var z = reader.ReadInt32();
        var yMin = reader.ReadSingle();
        var yMax = reader.ReadSingle();
        var vertices = ReadVertices(reader);
        var indices = ReadIndices(reader);
        var waterVertices = ReadVertices(reader);
        var waterIndices = ReadIndices(reader);
        var cyMin = ReadInts(reader);
        var cyMax = ReadInts(reader);

        return new LodMeshData(level, x, z, vertices, indices, waterVertices, waterIndices, yMin, yMax, cyMin, cyMax);
    }

    private static void WriteVertices(BinaryWriter writer, List<Vertex> vertices)
    {
        writer.Write(vertices.Count);
        if (vertices.Count > 0)
            writer.Write(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(vertices)));
    }

    private static List<Vertex> ReadVertices(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count <= 0)
            return [];
        var bytes = reader.ReadBytes(count * Marshal.SizeOf<Vertex>());
        return [.. MemoryMarshal.Cast<byte, Vertex>(bytes)];
    }

    private static void WriteIndices(BinaryWriter writer, List<ushort> indices)
    {
        writer.Write(indices.Count);
        if (indices.Count > 0)
            writer.Write(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(indices)));
    }

    private static List<ushort> ReadIndices(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count <= 0)
            return [];
        var bytes = reader.ReadBytes(count * sizeof(ushort));
        return [.. MemoryMarshal.Cast<byte, ushort>(bytes)];
    }

    private static void WriteInts(BinaryWriter writer, int[] values)
    {
        writer.Write(values.Length);
        foreach (var value in values)
            writer.Write(value);
    }

    private static int[] ReadInts(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        var values = new int[count];
        for (var i = 0; i < count; i++)
            values[i] = reader.ReadInt32();
        return values;
    }
}
