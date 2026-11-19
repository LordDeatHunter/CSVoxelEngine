using System;
using System.Numerics;
using VoxelEngine.Chunks.Meshing;
using VoxelEngine.Effects.Lighting;
using Xunit;

namespace VoxelEngine.Tests.Chunks.Meshing;

/// <summary>Registered ocean moss lighting through an explicit sealed water channel.</summary>
public sealed class OceanGlowMossLightFacts
{
    [Fact]
    public void GlowMossEmitsTealIntoWaterAndSeaMossDoesNot()
    {
        TestContent.EnsureLoaded();
        var glow = ContentRegistry.RequireBlock("glow_moss").Id;
        var moss = ContentRegistry.RequireBlock("sea_moss").Id;
        var water = ContentRegistry.RequireBlock("water").Id;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var teal = ContentRegistry.RequireBlock("fungal_glow").Id;
        var slot = BlockType.LightSlot[glow];

        Assert.Equal((byte)7, BlockType.Emission[glow]);
        Assert.Equal((byte)0, BlockType.ManMadeEmission[glow]);
        Assert.True(BlockType.ById[glow]!.NaturalLight);
        Assert.False(BlockType.PassesLightById[glow]);
        Assert.NotEqual((byte)0, slot);
        Assert.Equal(BlockType.LightSlot[teal], slot);
        var srgb = BlockLightPalette.SrgbOf(slot);
        Assert.InRange(srgb.X, 70f / 255f - 0.00001f, 70f / 255f + 0.00001f);
        Assert.InRange(srgb.Y, 240f / 255f - 0.00001f, 240f / 255f + 0.00001f);
        Assert.InRange(srgb.Z, 190f / 255f - 0.00001f, 190f / 255f + 0.00001f);
        Assert.Equal((byte)0, BlockType.Emission[moss]);

        const int x = 20, y = 20, z = 20;
        var ids = new ushort[Chunk.LightCount];
        Array.Fill(ids, stone); // Opaque on every side of the one-cell water path.
        for (var d = 1; d <= 7; d++) ids[Index(x + d, y, z)] = water;
        ids[Index(x, y, z)] = glow;
        var sun = new byte[Chunk.LightCount];
        var block = new byte[Chunk.LightCount];
        var queue = new int[Chunk.LightCount];
        var center = new Chunk(null!, Vector3.Zero, generateBlockData: false);

        try
        {
            // All nine supplied columns have terrain height 100, above this
            // volume's highest world Y=30. No sky seed or worldgen is needed.
            Assert.True(ChunkLighting.FillColumns(new CoveredColumns(), 0, 0,
                MeshScratch.Current.Columns));
            Assert.True(ChunkLighting.ComputeLight(center, ids, sun, block, queue));
            Assert.All(sun, level => Assert.Equal((byte)0, level));
            Assert.Equal((byte)(7 | slot << 4), block[Index(x, y, z)]);
            Assert.Equal((byte)(6 | slot << 4), block[Index(x + 1, y, z)]);
            Assert.Equal((byte)(1 | slot << 4), block[Index(x + 6, y, z)]);
            Assert.Equal((byte)0, block[Index(x + 7, y, z)]);
            Assert.Equal((byte)0, block[Index(x - 1, y, z)]); // Opaque neighbor.

            ids[Index(x, y, z)] = moss;
            Array.Clear(sun);
            Array.Clear(block);
            Array.Clear(queue);
            Assert.False(ChunkLighting.ComputeLight(center, ids, sun, block, queue));
            Assert.All(sun, level => Assert.Equal((byte)0, level));
            Assert.All(block, level => Assert.Equal((byte)0, level));
        }
        finally
        {
            MeshScratch.Current.ClearColumns();
        }
    }

    private static int Index(int x, int y, int z) => (x * Chunk.LightSize + y) * Chunk.LightSize + z;

    private sealed class CoveredColumns : IColumnSource
    {
        private readonly ColumnTerrain _terrain;

        public CoveredColumns()
        {
            var heights = new int[Chunk.Size * Chunk.Size];
            Array.Fill(heights, 100);
            _terrain = new ColumnTerrain(heights, new int[heights.Length], 0.5f, 0.5f);
        }

        public bool IsReady(int chunkX, int chunkZ) => true;

        public bool TryGet(int chunkX, int chunkZ, out ColumnTerrain terrain)
        {
            terrain = _terrain;
            return true;
        }
    }
}
