using System;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.WorldGen;

/// <summary>Frozen behavioral samples from the EA master before profile code existed.</summary>
[Collection("Worldgen seed")]
public sealed class WorldGenProfileIdentityTests
{
    public WorldGenProfileIdentityTests() => TestContent.EnsureLoaded();

    private static readonly int[] Seeds = [WorldSeed.Default, WorldSeed.Default + 7, WorldSeed.Default + 7919];

    // Origin, adjacent chunk seam, known jungle area (432,816), distant positive/negative
    // relief and coast candidates. The baseline records the actual biome and height at each.
    private static readonly (int X, int Z)[] Columns =
    [
        (0, 0), (1, 0), (27, 51), (28, 51), (-160, -160), (-159, -160),
        (93, -47), (-377, 382), (375, -373), (-12, 66), (160, 160), (0, -1),
    ];

    // Eight real generated chunks per seed, twenty-four total. Y=surface follows the actual
    // computed column; (93,-47) is an ocean at EA seed 1337, and cave/hell use fixed bands.
    private static readonly (int X, int Z, int YMode)[] Chunks =
    [
        (0, 0, 0), (1, 0, 0), (27, 51, 0), (28, 51, 0),
        (93, -47, 1), (-160, -160, 2), (-377, 382, 0), (375, -373, 3),
    ];

    private static string Digest(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true)) write(writer);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static void Write(BinaryWriter writer, int[] array)
    {
        writer.Write(array.Length);
        foreach (var value in array) writer.Write(value);
    }

    private static void Write(BinaryWriter writer, byte[] array)
    {
        writer.Write(array.Length);
        writer.Write(array);
    }

    private static void Write(BinaryWriter writer, float[] array)
    {
        writer.Write(array.Length);
        foreach (var value in array) writer.Write(BitConverter.SingleToInt32Bits(value));
    }

    private static string ColumnDigest(ColumnData column) => Digest(writer =>
    {
        Write(writer, column.Heights);
        Write(writer, column.DirtDepth);
        Write(writer, column.EntranceWeight);
        Write(writer, column.CaveIntensity);
        writer.Write(column.Biomes.Length);
        foreach (var biome in column.Biomes) writer.Write((byte)biome);
        Write(writer, column.Bed);
        Write(writer, column.WaterLevel);
        writer.Write(column.Oasis.Length);
        foreach (var oasis in column.Oasis) writer.Write(oasis);
        writer.Write(BitConverter.SingleToInt32Bits(column.CornerWarmth));
        writer.Write(BitConverter.SingleToInt32Bits(column.CornerWetness));
        writer.Write(column.MinHeight);
        writer.Write(column.MaxHeight);
        writer.Write(column.MaxWaterLevel);
    });

    // Names survive a content registry reorder; numeric block ids do not.
    private static string ChunkDigest(Chunk chunk) => Digest(writer =>
    {
        var blocks = chunk.Storage.ToDense();
        writer.Write(blocks.Length);
        foreach (var block in blocks)
            writer.Write(block == BlockType.Air ? "air" : BlockType.ById[block]?.Name
                ?? throw new InvalidDataException($"Unknown generated block id {block}"));
        writer.Write(chunk.StateArray != null);
        if (chunk.StateArray != null) Write(writer, chunk.StateArray);
        writer.Write(chunk.FluidFrontier != null);
        if (chunk.FluidFrontier != null)
        {
            writer.Write(chunk.FluidFrontier.Length);
            foreach (var index in chunk.FluidFrontier) writer.Write(index);
        }
        writer.Write(chunk.SolidCount);
        writer.Write(chunk.SurfaceMinY);
        writer.Write(chunk.IsBlockDataReady);
    });

    private static string[] Sample(WorldGenProfile profile)
    {
        var rows = new System.Collections.Generic.List<string>();
        foreach (var seed in Seeds)
        {
            WorldSeed.Apply(seed, profile);
            foreach (var (cx, cz) in Columns)
            {
                var column = ColumnCache.Get(cx, cz);
                rows.Add($"column,{seed},{cx},{cz},{column.MinHeight},{column.MaxHeight},{column.Biomes[136]},{ColumnDigest(column)}");
            }
            foreach (var (cx, cz, mode) in Chunks)
            {
                var column = ColumnCache.Get(cx, cz);
                var cy = mode switch
                {
                    0 => column.MaxHeight >> Chunk.SizeShift,
                    1 => WorldGenerationConfig.SeaLevel >> Chunk.SizeShift,
                    2 => -13,
                    _ => -38,
                };
                var chunk = new Chunk(null!, new Vector3(cx, cy, cz), generateBlockData: false);
                ChunkTerrainFill.GenerateBlockData(chunk);
                rows.Add($"chunk,{seed},{cx},{cy},{cz},{ChunkDigest(chunk)}");
            }
        }
        return rows.ToArray();
    }

    private static string[] ReadRows(TextReader reader)
    {
        var rows = new System.Collections.Generic.List<string>();
        while (reader.ReadLine() is { } row)
            if (row.Length > 0) rows.Add(row);
        return rows.ToArray();
    }

    [Fact]
    public void IdentityFixtureReadsBothLineEndings()
    {
        using var resource = typeof(WorldGenProfileIdentityTests).Assembly.GetManifestResourceStream(
            "VoxelEngine.Tests.WorldGen.LegacyEa201Identity.csv");
        Assert.NotNull(resource);
        using var reader = new StreamReader(resource);
        var rows = ReadRows(reader);
        Assert.Equal(60, rows.Length);
        Assert.Equal(rows, ReadRows(new StringReader(string.Join("\n", rows) + "\n")));
        Assert.Equal(rows, ReadRows(new StringReader(string.Join("\r\n", rows) + "\r\n")));
    }

    [Fact]
    public void LegacyGenerationMatchesFrozenEaMaster()
    {
        Assert.False(WorldSeed.WorldIsLive);
        try
        {
            using var resource = typeof(WorldGenProfileIdentityTests).Assembly.GetManifestResourceStream(
                "VoxelEngine.Tests.WorldGen.LegacyEa201Identity.csv");
            Assert.NotNull(resource);
            using var reader = new StreamReader(resource);
            var expected = ReadRows(reader);
            // S0 Dynamic intentionally delegates to the EA generator. Legacy runs after a
            // Dynamic -> Legacy transition, so stale profile-dependent cache entries show up.
            Assert.Equal(expected, Sample(WorldGenProfile.Dynamic));
            Assert.Equal(expected, Sample(WorldGenProfile.Legacy));
        }
        finally
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
    }
}
