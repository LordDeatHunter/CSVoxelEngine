using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Data;
using VoxelEngine.Lod;
using VoxelEngine.Networking;
using Xunit;

namespace VoxelEngine.Tests.Networking;

/// <summary>
/// The far-LOD wire codec: a server-built region mesh must survive the round trip exactly, since a
/// client renders it without being able to build it (custom/premade server world).
/// </summary>
public sealed class LodNetCodecTests
{
    [Fact]
    public void ARegionMeshRoundTripsExactly()
    {
        var vertices = new List<Vertex>
        {
            new(new Vector3(1f, 2f, 3f), 10, 20, 30, 255, new Vector2(0.5f, 0.25f), (ushort)7),
            new(new Vector3(-4f, 5f, 6f), 0, 0, 0, 128, new Vector2(2f, 3f), (ushort)0),
        };
        var indices = new List<ushort> { 0, 1, 2, 2, 1, 3 };
        var waterVertices = new List<Vertex>
        {
            new(new Vector3(0f, 60f, 0f), 1, 2, 3, 254, new Vector2(1f, 1f), (ushort)5),
        };
        var waterIndices = new List<ushort> { 0, 0, 0 };
        var source = new LodMeshData(2, -3, 4, vertices, indices, waterVertices, waterIndices,
            40f, 90f, [1, 2, 3], [4, 5, 6]);

        var decoded = LodNetCodec.Decode(LodNetCodec.Encode(source));

        Assert.Equal(source.Level, decoded.Level);
        Assert.Equal(source.X, decoded.X);
        Assert.Equal(source.Z, decoded.Z);
        Assert.Equal(source.YMin, decoded.YMin);
        Assert.Equal(source.YMax, decoded.YMax);
        Assert.Equal(source.ColumnCyMin, decoded.ColumnCyMin);
        Assert.Equal(source.ColumnCyMax, decoded.ColumnCyMax);
        Assert.Equal(source.Indices, decoded.Indices);
        Assert.Equal(source.WaterIndices, decoded.WaterIndices);

        Assert.Equal(vertices.Count, decoded.Vertices.Count);
        Assert.Equal(vertices[0].Position, decoded.Vertices[0].Position);
        Assert.Equal(vertices[0].PackedColor, decoded.Vertices[0].PackedColor);
        Assert.Equal(vertices[0].Uv, decoded.Vertices[0].Uv);
        Assert.Equal(vertices[0].Layer, decoded.Vertices[0].Layer);
        Assert.Equal(vertices[0].Normal, decoded.Vertices[0].Normal);
        Assert.Equal(vertices[0].LightTint, decoded.Vertices[0].LightTint);
        Assert.Equal(waterVertices.Count, decoded.WaterVertices.Count);
    }

    [Fact]
    public void AnEmptyRegionRoundTrips()
    {
        var source = new LodMeshData(0, 0, 0, [], [], [], [], 0f, 0f, [], []);

        var decoded = LodNetCodec.Decode(LodNetCodec.Encode(source));

        Assert.Empty(decoded.Vertices);
        Assert.Empty(decoded.Indices);
        Assert.Empty(decoded.WaterVertices);
    }
}
