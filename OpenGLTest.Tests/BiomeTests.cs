using System;
using System.Collections.Generic;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Covers the biome split: selection from continentalness/aridity, the ocean depth ramp and its
/// floor bands, the desert sand/sandstone column, and the cactus wood family (which must never
/// weld to tree wood). The near-terrain and far-LOD views are checked separately.
/// </summary>
public sealed class BiomeTests
{
    public BiomeTests() => TestContent.EnsureLoaded();

    /// <summary>The surface chunk of a world column and the height's local Y inside it.</summary>
    private static (Chunk Chunk, int Ly) SurfaceChunk(int x, int z, out int height)
    {
        height = Chunk.GetColumnHeights(x >> Chunk.SizeShift, z >> Chunk.SizeShift)
            [((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask)];
        var cy = height >> Chunk.SizeShift;
        var chunk = new Chunk(null!, new Vector3(x >> Chunk.SizeShift, cy, z >> Chunk.SizeShift),
            generateBlockData: true);
        return (chunk, height - cy * Chunk.Size);
    }

    [Fact]
    public void OriginColumnIsForest()
    {
        Assert.Equal(Biome.Forest, TerrainGenerator.BiomeAt(0, 0));
        Assert.True(TerrainGenerator.IsForest(0, 0));
    }

    [Fact]
    public void BiomeClassificationIsDeterministic()
    {
        for (var x = -600; x <= 600; x += 37)
        for (var z = -600; z <= 600; z += 41)
            Assert.Equal(TerrainGenerator.BiomeAt(x, z), TerrainGenerator.BiomeAt(x, z));
    }

    [Fact]
    public void OceanAndDesertBothExistNearOrigin()
    {
        var sawOcean = false;
        var sawDesert = false;
        const int radius = 64;
        for (var cx = 0; cx <= radius && !(sawOcean && sawDesert); cx++)
        for (var cz = -radius; cz <= radius && !(sawOcean && sawDesert); cz++)
        {
            var biome = TerrainGenerator.BiomeAt(cx * Chunk.Size + 8, cz * Chunk.Size + 8);
            sawOcean |= biome == Biome.Ocean;
            sawDesert |= biome == Biome.Desert;
        }

        Assert.True(sawOcean, "no ocean biome within 64 chunks");
        Assert.True(sawDesert, "no desert biome within 64 chunks");
    }

    [Fact]
    public void OceanFloorWearsItsDepthMaterial()
    {
        var sand = ContentRegistry.RequireBlock("sand").Id;
        var gravel = ContentRegistry.RequireBlock("gravel").Id;
        var rock = ContentRegistry.RequireBlock("stone").Id;
        // The ore pass replaces deep stone at any depth, so a stone-band floor may read as ore
        var ore = ContentRegistry.RequireBlock("malachite_ore").Id;

        var sawSand = false;
        var sawGravel = false;
        var sawStone = false;
        const int radius = 64;

        for (var cx = -radius; cx <= radius && !(sawSand && sawGravel && sawStone); cx++)
        for (var cz = -radius; cz <= radius && !(sawSand && sawGravel && sawStone); cz++)
        {
            if (TerrainGenerator.BiomeAt(cx * Chunk.Size + 8, cz * Chunk.Size + 8) != Biome.Ocean)
                continue;

            for (var x = 0; x < Chunk.Size; x++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var worldX = cx * Chunk.Size + x;
                var worldZ = cz * Chunk.Size + z;
                if (TerrainGenerator.BiomeAt(worldX, worldZ) != Biome.Ocean)
                    continue;

                var (chunk, ly) = SurfaceChunk(worldX, worldZ, out var height);
                var id = chunk.GetLocalBlock(x, ly, z);
                if (id == BlockType.Air || BlockType.IsFluid(id))
                    continue; // a cave or entrance opened the sea floor; try another column

                var expected = height >= WorldGenerationConfig.SeaLevel - WorldGenerationConfig.OceanSandDepth
                    ? sand
                    : height >= WorldGenerationConfig.SeaLevel - WorldGenerationConfig.OceanGravelDepth
                        ? gravel
                        : rock;
                Assert.True(id == expected || (expected == rock && id == ore),
                    $"ocean floor at ({worldX},{height},{worldZ}) is block {id}, expected {expected}");
                sawSand |= expected == sand;
                sawGravel |= expected == gravel;
                sawStone |= expected == rock;
            }
        }

        Assert.True(sawSand && sawGravel && sawStone,
            $"ocean floor bands seen: sand={sawSand} gravel={sawGravel} stone={sawStone}");
    }

    [Fact]
    public void DesertColumnIsSandOverSandstone()
    {
        var sand = ContentRegistry.RequireBlock("sand").Id;
        var sandstone = ContentRegistry.RequireBlock("sandstone").Id;
        var verified = 0;

        for (var cx = -64; cx <= 64 && verified < 3; cx++)
        for (var cz = -64; cz <= 64 && verified < 3; cz++)
        {
            var column = Chunk.GetColumnData(cx, cz);
            for (var x = 0; x < Chunk.Size && verified < 3; x++)
            for (var z = 0; z < Chunk.Size && verified < 3; z++)
            {
                var li = (x << Chunk.SizeShift) | z;
                if (column.Biomes[li] != Biome.Desert) continue;
                if (column.DirtDepth[li] == 0) continue; // ravine-stripped: bare rock
                if (TerrainGenerator.IsBeachHeight(column.Heights[li])) continue;

                var (chunk, ly) = SurfaceChunk(cx * Chunk.Size + x, cz * Chunk.Size + z, out _);
                if (chunk.GetLocalBlock(x, ly, z) != sand) continue; // carved: try another

                var below = ly - 1;
                while (below >= 0 && chunk.GetLocalBlock(x, below, z) == BlockType.Air)
                    below--;
                if (below < 0 || chunk.GetLocalBlock(x, below, z) != sandstone)
                    continue; // the sandstone band was carved away here

                verified++;
            }
        }

        Assert.True(verified > 0, "no clean desert sand-over-sandstone column found");
    }

    [Fact]
    public void ForestColumnTopsWithGrass()
    {
        var grass = ContentRegistry.RequireBlock("grass").Id;
        var verified = 0;

        for (var cx = -8; cx <= 8 && verified < 3; cx++)
        for (var cz = -8; cz <= 8 && verified < 3; cz++)
        {
            var column = Chunk.GetColumnData(cx, cz);
            for (var x = 0; x < Chunk.Size && verified < 3; x++)
            for (var z = 0; z < Chunk.Size && verified < 3; z++)
            {
                var li = (x << Chunk.SizeShift) | z;
                if (column.Biomes[li] != Biome.Forest) continue;
                if (column.DirtDepth[li] == 0) continue;
                if (TerrainGenerator.IsBeachHeight(column.Heights[li])) continue;
                if (TerrainGenerator.IsSubmerged(column.Heights[li])) continue;

                var (chunk, ly) = SurfaceChunk(cx * Chunk.Size + x, cz * Chunk.Size + z, out _);
                if (chunk.GetLocalBlock(x, ly, z) != grass) continue;

                verified++;
            }
        }

        Assert.True(verified > 0, "no clean forest grass-top column found");
    }

    [Fact]
    public void DesertsGrowNoTreesAndForestsGrowNoCacti()
    {
        var desert = FindDesertChunk();
        Assert.NotNull(desert);

        // No tree may take root in the desert...
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var worldX = desert.Value.Cx * Chunk.Size + x;
            var worldZ = desert.Value.Cz * Chunk.Size + z;
            if (TerrainGenerator.BiomeAt(worldX, worldZ) == Biome.Desert)
                Assert.False(TreeGenerator.TryGetTrunk(worldX, worldZ, out _, out _));
        }

        // ...and no cactus may take root in the origin's forest column.
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            if (TerrainGenerator.IsForest(x, z))
                Assert.False(CactusGenerator.TryGetCactus(x, z, out _, out _));
    }

    private static (int Cx, int Cz)? FindDesertChunk()
    {
        for (var cx = 0; cx <= 64; cx++)
        for (var cz = -64; cz <= 64; cz++)
            if (TerrainGenerator.BiomeAt(cx * Chunk.Size + 8, cz * Chunk.Size + 8) == Biome.Desert)
                return (cx, cz);
        return null;
    }

    [Fact]
    public void CactiGrowInDesert()
    {
        var checkedChunks = 0;
        for (var cx = 0; cx <= 64 && checkedChunks < 24; cx++)
        for (var cz = -64; cz <= 64 && checkedChunks < 24; cz++)
        {
            if (TerrainGenerator.BiomeAt(cx * Chunk.Size + 8, cz * Chunk.Size + 8) != Biome.Desert)
                continue;
            checkedChunks++;

            for (var x = 0; x < Chunk.Size; x++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                if (!CactusGenerator.TryGetCactus(cx * Chunk.Size + x, cz * Chunk.Size + z, out _, out var shape))
                    continue;

                Assert.NotEmpty(shape.Wood);
                foreach (var block in shape.Wood)
                    Assert.Equal(BlockType.CactusWoodFamily, BlockType.WoodFamilyOf(block.Id));

                return;
            }
        }

        Assert.Fail("no cactus found in 24 desert chunks");
    }

    [Fact]
    public void CactusBlocksFormAThreeStepFamily()
    {
        var stem = ContentRegistry.RequireBlock("cactus");
        var arm = ContentRegistry.RequireBlock("cactus_arm");
        var tip = ContentRegistry.RequireBlock("cactus_tip");

        Assert.Equal(0, stem.WoodTaperLevel);
        Assert.Equal(1, arm.WoodTaperLevel);
        Assert.Equal(2, tip.WoodTaperLevel);

        // The stem is the thickest size but still a column, not a full cube
        Assert.True(stem.HalfWidth < 0.5f, $"stem half-width {stem.HalfWidth}");
        Assert.True(stem.HalfWidth > arm.HalfWidth && arm.HalfWidth > tip.HalfWidth);

        Assert.Equal(BlockType.CactusWoodFamily, BlockType.WoodFamilyOf(stem.Id));
        Assert.Equal(BlockType.CactusWoodFamily, BlockType.WoodFamilyOf(arm.Id));
        Assert.Equal(BlockType.CactusWoodFamily, BlockType.WoodFamilyOf(tip.Id));
        Assert.Equal(BlockType.TreeWoodFamily, BlockType.WoodFamilyOf(ContentRegistry.RequireBlock("log").Id));
    }

    [Fact]
    public void WoodFamiliesNeverWeld()
    {
        var log = ContentRegistry.RequireBlock("log").Id;
        var stem = ContentRegistry.RequireBlock("cactus").Id;
        var arm = ContentRegistry.RequireBlock("cactus_arm").Id;

        Assert.False(WoodShape.ConnectsVertical(log, stem));
        Assert.False(WoodShape.ConnectsVertical(stem, log));
        Assert.False(WoodShape.ConnectsHorizontal(log, stem));

        // ...but a cactus welds to itself, and a prong to the stem it leaves
        Assert.True(WoodShape.ConnectsVertical(stem, stem));
        Assert.True(WoodShape.ConnectsHorizontal(stem, arm));
        Assert.True(WoodShape.ConnectsVertical(arm, arm));
    }

    [Fact]
    public void LodWearsDesertSandAndCactusHasNoLeaves()
    {
        // Far LOD must show the desert as sand where the full-detail chunks do.
        var sand = ContentRegistry.RequireBlock("sand").FaceTiles[4];
        for (var cx = 0; cx <= 64; cx++)
        for (var cz = -64; cz <= 64; cz++)
        {
            if (TerrainGenerator.BiomeAt(cx * Chunk.Size + 8, cz * Chunk.Size + 8) != Biome.Desert)
                continue;

            const int level = 1;
            var regionBlocks = LodRegion.Levels[level].RegionBlocks;
            var mesh = LodRegion.BuildMeshData(level, cx / (regionBlocks / Chunk.Size), cz / (regionBlocks / Chunk.Size));
            Assert.Contains(mesh.Vertices, v => v.Layer == sand);
            return;
        }

        Assert.Fail("no desert chunk found for the LOD check");
    }
}
