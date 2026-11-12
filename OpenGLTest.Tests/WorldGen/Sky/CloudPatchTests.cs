using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Sky;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Sky;

/// <summary>
/// The solid cloud patches (plans/LAYERS.md L-11): cloud only inside its config slab and only in
/// air, a patchy footprint whose edges are thin and whose cores are thicker, a chunk stamp that
/// equals the point query across chunk seams, sky chunks in the slab generated non-empty, and
/// patches that follow the world seed.
/// </summary>
[Collection("Worldgen seed")]
public sealed class CloudPatchTests
{
    private const int Volume = Chunk.Size * Chunk.Size * Chunk.Size;

    public CloudPatchTests() => TestContent.EnsureLoaded();

    private static int SlabChunkY => WorldGenerationConfig.CloudSlabBottomY >> Chunk.SizeShift;

    /// <summary>The chunk column nearest the origin whose center column holds cloud.</summary>
    private static (int Cx, int Cz) CloudyChunkColumn()
    {
        for (var ring = 0; ring < 64; ring++)
        for (var cx = -ring; cx <= ring; cx++)
        for (var cz = -ring; cz <= ring; cz++)
        {
            if (Math.Max(Math.Abs(cx), Math.Abs(cz)) != ring) continue;
            if (CloudPatchGenerator.TryGetColumn(cx * Chunk.Size + 8, cz * Chunk.Size + 8, out _, out _))
                return (cx, cz);
        }

        throw new InvalidOperationException("no cloud within 64 chunk columns of the origin");
    }

    private static Chunk Generate(int cx, int cy, int cz)
    {
        var chunk = new Chunk(null!, new Vector3(cx, cy, cz), generateBlockData: false);
        ChunkTerrainFill.GenerateBlockData(chunk);
        return chunk;
    }

    [Fact]
    public void EveryCloudColumnLiesInsideTheSlabAndIsThinAtItsEdgesAndThickerInItsCore()
    {
        var with = 0;
        var without = 0;
        var thin = 0;
        var thick = 0;
        for (var x = -256; x < 256; x += 2)
        for (var z = -256; z < 256; z += 2)
        {
            if (!CloudPatchGenerator.TryGetColumn(x, z, out var bottom, out var top))
            {
                without++;
                continue;
            }

            with++;
            Assert.InRange(bottom, WorldGenerationConfig.CloudSlabBottomY, top);
            Assert.InRange(top, bottom, WorldGenerationConfig.CloudSlabTopY);
            var thickness = top - bottom + 1;
            if (thickness == 1) thin++;
            if (thickness > CloudPatchGenerator.SlabHeight / 2) thick++;
        }

        // Patchy: open sky between the patches, and both thin rims and thick cores within them
        Assert.True(with > 0 && without > 0, $"{with} cloud and {without} open columns");
        Assert.True(thin > 0 && thick > 0, $"{thin} one-cell and {thick} thick columns");
    }

    [Fact]
    public void TheStampWritesOnlyAirInsideTheSlab()
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var (cx, cz) = CloudyChunkColumn();
        var blocks = new ushort[Volume];

        for (var cy = SlabChunkY - 1; cy <= SlabChunkY + 1; cy++)
        {
            var y0 = cy * Chunk.Size;

            // Over solid ground it writes nothing at all
            Array.Fill(blocks, stone);
            CloudPatchGenerator.Stamp(blocks, cx * Chunk.Size, y0, cz * Chunk.Size);
            Assert.All(blocks, b => Assert.Equal(stone, b));

            // Into air, only inside the slab
            Array.Clear(blocks);
            CloudPatchGenerator.Stamp(blocks, cx * Chunk.Size, y0, cz * Chunk.Size);
            for (var i = 0; i < Volume; i++)
            {
                if (blocks[i] == BlockType.Air) continue;
                Assert.Equal(CloudPatchGenerator.CloudBlock, blocks[i]);
                Assert.InRange(y0 + ((i >> Chunk.SizeShift) & Chunk.SizeMask),
                    WorldGenerationConfig.CloudSlabBottomY, WorldGenerationConfig.CloudSlabTopY);
            }
        }
    }

    [Fact]
    public void GeneratedSkyChunksEqualThePointQueryAcrossSeams()
    {
        var (cx, cz) = CloudyChunkColumn();
        var sawCloud = false;
        for (var dcx = 0; dcx <= 1; dcx++)
        for (var dcz = 0; dcz <= 1; dcz++)
        for (var cy = SlabChunkY - 1; cy <= SlabChunkY + 1; cy++)
        {
            var chunk = Generate(cx + dcx, cy, cz + dcz);
            Assert.True(chunk.IsBlockDataReady);
            var clouds = 0;
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var wx = (cx + dcx) * Chunk.Size + x;
                var wy = cy * Chunk.Size + y;
                var wz = (cz + dcz) * Chunk.Size + z;
                var block = chunk.GetLocalBlock(x, y, z);
                // The sky up here is empty but for the cloud
                Assert.Equal(CloudPatchGenerator.IsCloud(wx, wy, wz) ? CloudPatchGenerator.CloudBlock : BlockType.Air, block);
                if (block != BlockType.Air) clouds++;
            }

            // A chunk holding cloud counts it, so it meshes like any other non-empty chunk
            Assert.Equal(clouds, chunk.SolidCount);
            sawCloud |= clouds > 0;
        }

        Assert.True(sawCloud, "the cloudy column's chunks held no cloud");
    }

    [Fact]
    public void ThePatchesFollowTheWorldSeed()
    {
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
            var first = CloudCells();
            WorldSeed.Apply(WorldSeed.Default + 7);
            Assert.False(first.SetEquals(CloudCells()));
            WorldSeed.Apply(WorldSeed.Default);
            Assert.True(first.SetEquals(CloudCells()));
            Assert.NotEmpty(first);
        }
        finally { WorldSeed.Apply(WorldSeed.Default); }
    }

    private static HashSet<(int, int, int)> CloudCells()
    {
        var set = new HashSet<(int, int, int)>();
        for (var x = 0; x < 256; x += 4)
        for (var z = 0; z < 256; z += 4)
        for (var y = WorldGenerationConfig.CloudSlabBottomY; y <= WorldGenerationConfig.CloudSlabTopY; y++)
            if (CloudPatchGenerator.IsCloud(x, y, z)) set.Add((x, y, z));
        return set;
    }
}
