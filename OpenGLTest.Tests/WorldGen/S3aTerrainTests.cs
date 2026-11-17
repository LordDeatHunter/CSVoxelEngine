using System;
using System.Numerics;
using VoxelEngine.Content;
using VoxelEngine.Lod;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using VoxelEngine.WorldGen.Trees.Shape;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen;

/// <summary>Behavior of naturally generated Dynamic sand-sea columns.</summary>
[Collection("Worldgen seed")]
public sealed class S3aTerrainTests(ITestOutputHelper output)
{
    [Fact]
    public void CoreVerdictAndDuneHeightRepeatAfterSeedAndProfileReset()
    {
        TestContent.EnsureLoaded();
        var direct = 0;
        try
        {
            const int x = -8184;
            const int z = 2312;
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            Assert.True(TerrainErg.IsCore(x >> Chunk.SizeShift, z >> Chunk.SizeShift));
            direct++;
            var height = TerrainGenerator.LandHeight(x, z, out _, out _, out _, out _);
            direct++;
            WorldSeed.Apply(1337, WorldGenProfile.Legacy);
            Assert.False(TerrainErg.IsCore(x >> Chunk.SizeShift, z >> Chunk.SizeShift));
            direct++;
            WorldSeed.Apply(42, WorldGenProfile.Dynamic);
            _ = TerrainErg.IsCore(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
            direct++;
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            Assert.True(TerrainErg.IsCore(x >> Chunk.SizeShift, z >> Chunk.SizeShift));
            direct++;
            Assert.Equal(height, TerrainGenerator.LandHeight(x, z, out _, out _, out _, out _));
            direct++;
            output.WriteLine($"reset height={height:F3}");
        }
        finally
        {
            output.WriteLine($"S3a reset direct={direct} normal_fills=0");
            var (builds, biomes) = TerrainErg.CoreReadCounts;
            output.WriteLine($"S3a internal core_builds={builds} stored_biomes={biomes}");
            WorldSeed.Apply(WorldSeed.Default);
        }
    }

    [Fact]
    public void GeneratedFarCoreKeepsSandOnExposedSides()
    {
        TestContent.EnsureLoaded();
        try
        {
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            const int x = -8184;
            const int z = 2312;
            var spec = LodRegion.Levels[0];
            var rx = (int)MathF.Floor((float)x / spec.RegionBlocks);
            var rz = (int)MathF.Floor((float)z / spec.RegionBlocks);
            var mesh = LodRegion.BuildMeshData(0, rx, rz);
            var sand = VoxelEngine.BlockType.ById[ContentRegistry.RequireBlock("sand").Id]!.FaceTiles[0];
            var sideSand = 0;
            foreach (var vertex in mesh.Vertices)
            {
                if ((vertex.Normal & 7) == 4 || vertex.Layer != sand) continue;
                if (vertex.Position.X >= 0 && vertex.Position.X <= Chunk.Size
                    && vertex.Position.Z >= 0 && vertex.Position.Z <= Chunk.Size)
                    sideSand++;
            }
            output.WriteLine($"generated core LOD sand-side vertices={sideSand}; direct cells={spec.Cells * spec.Cells}; normal_fills=0");
            Assert.True(sideSand > 0);
        }
        finally
        {
            var (builds, biomes) = TerrainErg.CoreReadCounts;
            output.WriteLine($"S3a internal core_builds={builds} stored_biomes={biomes}");
            WorldSeed.Apply(WorldSeed.Default);
        }
    }

    [Fact]
    public void WindCrossSectionHasGentleStossAndSteeperSlipFace()
    {
        TestContent.EnsureLoaded();
        var direct = 0;
        try
        {
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            const int x0 = -8184;
            const int z0 = 2312;
            var priorDune = TerrainErg.DuneDelta(x0, z0, 1f);
            var priorHeight = TerrainGenerator.LandHeight(x0, z0, out _, out _, out _, out _);
            direct += 2;
            var maxRise = 0f;
            var maxFall = 0f;
            var minHeight = priorHeight;
            var maxHeight = priorHeight;
            for (var t = 1; t <= 240; t++)
            {
                // Advance along the wind, not across an arbitrary noise ridge.
                var x = x0 + t * 0.8f;
                var z = z0 + t * 0.6f;
                var dune = TerrainErg.DuneDelta(x, z, 1f);
                var height = TerrainGenerator.LandHeight(x, z, out _, out _, out _, out _);
                direct += 2;
                maxRise = Math.Max(maxRise, dune - priorDune);
                maxFall = Math.Max(maxFall, priorDune - dune);
                minHeight = Math.Min(minHeight, height);
                maxHeight = Math.Max(maxHeight, height);
                priorDune = dune;
            }
            output.WriteLine($"wind section rise={maxRise:F3} fall={maxFall:F3} actual_height_span={maxHeight - minHeight:F3}");
            Assert.True(maxFall > maxRise * 1.5f);
            Assert.True(maxHeight - minHeight > 5f);
        }
        finally
        {
            output.WriteLine($"S3a wind direct={direct} normal_fills=0");
            var (builds, biomes) = TerrainErg.CoreReadCounts;
            output.WriteLine($"S3a internal core_builds={builds} stored_biomes={biomes}");
            WorldSeed.Apply(WorldSeed.Default);
        }
    }

    [Fact]
    public void WholeTreeAndOasisSitesOutsideCoreCannotIntrude()
    {
        TestContent.EnsureLoaded();
        var direct = 0;
        try
        {
            WorldSeed.Apply(-4242, WorldGenProfile.Dynamic);
            const int treeX = -6666;
            const int treeZ = -513;
            const int treeReach = 9; // observed from the real species on the gate-disabled control
            Assert.False(TerrainErg.IsCore(treeX >> Chunk.SizeShift, treeZ >> Chunk.SizeShift));
            direct++;
            Assert.True(TerrainErg.Intersects(treeX - treeReach, treeZ - treeReach,
                treeX + treeReach, treeZ + treeReach));
            direct++;
            Assert.False(TreeGenerator.TryGetTree(treeX, treeZ, out _, out _));
            direct++;
            var (treeBuilds, treeBiomes) = TerrainErg.CoreReadCounts;
            output.WriteLine($"S3a tree internal core_builds={treeBuilds} stored_biomes={treeBiomes}");

            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            const int oasisCellX = -64;
            const int oasisCellZ = -57;
            const int oasisX = -8172;
            const int oasisZ = -7267;
            var r = WorldFeatureConfig.OasisRadius;
            Assert.False(TerrainErg.IsCore(oasisX >> Chunk.SizeShift, oasisZ >> Chunk.SizeShift));
            direct++;
            Assert.True(TerrainErg.IntersectsDisc(oasisX, oasisZ, r));
            direct++;
            Assert.False(OasisGenerator.TryGetCenter(oasisCellX, oasisCellZ, out _, out _, out _));
            direct++;
        }
        finally
        {
            output.WriteLine($"S3a intrusion direct={direct} normal_fills=0");
            var (builds, biomes) = TerrainErg.CoreReadCounts;
            output.WriteLine($"S3a internal core_builds={builds} stored_biomes={biomes}");
            WorldSeed.Apply(WorldSeed.Default);
        }
    }

    [Fact]
    public void ThreeSeedsContainConnectedHardCoreColumns()
    {
        TestContent.EnsureLoaded();
        var direct = 0;
        try
        {
            // Sites came from the bounded 48 x 32 grid at PR71 + candidate code.
            foreach (var (seed, cx, cz) in new[]
            {
                (1337, -512, -448), (42, -432, -432), (-4242, -416, -32)
            })
            {
                WorldSeed.Apply(seed, WorldGenProfile.Dynamic);
                for (var dx = 0; dx < 2; dx++)
                for (var dz = 0; dz < 2; dz++)
                {
                    direct++;
                    Assert.True(TerrainErg.IsCore(cx + dx, cz + dz),
                        $"Missing connected core seed={seed} chunk=({cx + dx},{cz + dz})");
                }
                output.WriteLine($"connected seed={seed} chunk=({cx},{cz})");
                var (seedBuilds, seedBiomes) = TerrainErg.CoreReadCounts;
                output.WriteLine($"connected internal seed={seed} core_builds={seedBuilds} stored_biomes={seedBiomes}");
            }
        }
        finally
        {
            output.WriteLine($"S3a connected direct={direct} normal_fills=0");
            var (builds, biomes) = TerrainErg.CoreReadCounts;
            output.WriteLine($"S3a internal core_builds={builds} stored_biomes={biomes}");
            WorldSeed.Apply(WorldSeed.Default);
        }
    }

    [Fact]
    public void HardCoreHasDeepSandInActualFilledColumn()
    {
        TestContent.EnsureLoaded();
        var direct = 0;
        var chunks = 0;
        try
        {
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            // Frozen from the parent fail-first test-only field; production must own the verdict.
            const int x = -8184;
            const int z = 2312;
            Assert.True(TerrainErg.IsCore(x >> Chunk.SizeShift, z >> Chunk.SizeShift));
            direct++;
            var column = ColumnCache.Get(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
            direct++;
            var i = ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);
            Assert.Equal(Biome.Desert, column.Biomes[i]);
            var top = column.Heights[i];
            Assert.True((top & Chunk.SizeMask) >= WorldGenerationConfig.SandstoneDepth + 6);
            var chunk = new Chunk(null!, new Vector3(x >> Chunk.SizeShift, top >> Chunk.SizeShift,
                z >> Chunk.SizeShift), generateBlockData: false);
            chunks++;
            ChunkTerrainFill.GenerateBlockData(chunk);
            var sandCells = 0;
            for (var depth = 0; depth <= WorldGenerationConfig.SandstoneDepth + 6; depth++)
            {
                direct++;
                var block = chunk.Storage.Get(Chunk.Idx(x & Chunk.SizeMask, (top - depth) & Chunk.SizeMask,
                    z & Chunk.SizeMask));
                if (block == BlockType.Air) continue; // caves retain carve ownership
                Assert.Equal(TerrainGenerator.SandBlock, block);
                sandCells++;
            }
            Assert.True(sandCells >= 3);
            output.WriteLine($"core seed=1337 x={x} z={z} top={top} sand_cells={sandCells}");
        }
        finally
        {
            output.WriteLine($"S3a filled direct={direct} normal_fills={chunks}");
            var (builds, biomes) = TerrainErg.CoreReadCounts;
            output.WriteLine($"S3a internal core_builds={builds} stored_biomes={biomes}");
            WorldSeed.Apply(WorldSeed.Default);
        }
    }

}
