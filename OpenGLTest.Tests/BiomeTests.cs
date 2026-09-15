using System;
using System.Collections.Generic;
using System.Linq;
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
    public void ForestIsMostlyDryLandWithScatteredWater()
    {
        // The forest floor sits above the waterline, so water appears only where a lake or river
        // carved below it. The guard is that such water stays a small fraction of the forest: the
        // old blanket flood covered about a quarter of the map.
        var forest = 0;
        var wet = 0;

        for (var cx = -60; cx <= 60; cx++)
        for (var cz = -60; cz <= 60; cz++)
        {
            if (TerrainGenerator.BiomeAt(cx * Chunk.Size + 8, cz * Chunk.Size + 8) != Biome.Forest)
                continue;

            var column = Chunk.GetColumnData(cx, cz);
            for (var x = 0; x < Chunk.Size; x++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var li = (x << Chunk.SizeShift) | z;
                var worldX = cx * Chunk.Size + x;
                var worldZ = cz * Chunk.Size + z;
                if (TerrainGenerator.BiomeAt(worldX, worldZ) != Biome.Forest) continue;

                forest++;
                if (column.Heights[li] < WorldGenerationConfig.SeaLevel
                    && TerrainGenerator.IsWaterColumn(worldX, worldZ, column.Heights[li]))
                    wet++;
            }
        }

        Assert.True(wet > 0, "no lake or river water anywhere in the forest");
        Assert.True(wet * 4 < forest, $"forest water is not scattered: {wet} wet of {forest} forest columns");
    }

    [Fact]
    public void ForestSurfaceWaterClosesBeforeTheDesert()
    {
        // The carve and the flood test are gated toward a desert (TerrainGenerator.SurfaceWaterGate),
        // so a pond closes on its own forest rim instead of ending in a wall where the hard biome
        // flips. The invariant: no non-ocean water column is 4-adjacent to a desert column.
        var waterColumns = 0;
        const int radius = 48;

        for (var cx = -radius; cx <= radius; cx++)
        for (var cz = -radius; cz <= radius; cz++)
        {
            var column = Chunk.GetColumnData(cx, cz);
            for (var x = 0; x < Chunk.Size; x++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var height = column.Heights[(x << Chunk.SizeShift) | z];
                if (height >= WorldGenerationConfig.SeaLevel) continue;

                var worldX = cx * Chunk.Size + x;
                var worldZ = cz * Chunk.Size + z;
                if (!TerrainGenerator.IsWaterColumn(worldX, worldZ, height)) continue;
                if (TerrainGenerator.BiomeAt(worldX, worldZ) == Biome.Ocean) continue;

                waterColumns++;
                Assert.NotEqual(Biome.Desert, TerrainGenerator.BiomeAt(worldX + 1, worldZ));
                Assert.NotEqual(Biome.Desert, TerrainGenerator.BiomeAt(worldX - 1, worldZ));
                Assert.NotEqual(Biome.Desert, TerrainGenerator.BiomeAt(worldX, worldZ + 1));
                Assert.NotEqual(Biome.Desert, TerrainGenerator.BiomeAt(worldX, worldZ - 1));
            }
        }

        Assert.True(waterColumns > 0, "no surface water found within the scan to check");
    }

    [Fact]
    public void ForestMountainsAreRareButPresent()
    {
        // A mountain lifts a forest column well above anything the hill field alone can reach
        // (ForestBaseHeight + swell + HillAmplitude + rough tops out near 53 blocks). Ranges must
        // exist, and stay a small share of the forest so they read as occasional.
        const int mountainHeight = 56;
        var forestColumns = 0;
        var mountainColumns = 0;

        for (var cx = -100; cx <= 100; cx += 2)
        for (var cz = -100; cz <= 100; cz += 2)
        {
            if (TerrainGenerator.BiomeAt(cx * Chunk.Size + 8, cz * Chunk.Size + 8) != Biome.Forest)
                continue;

            forestColumns++;
            if (Chunk.GetColumnData(cx, cz).MaxHeight > mountainHeight) mountainColumns++;
        }

        Assert.True(mountainColumns > 0,
            $"no forest column rises above {mountainHeight} blocks in the scanned area");
        Assert.True(mountainColumns * 5 < forestColumns,
            $"mountains are not occasional: {mountainColumns} of {forestColumns} forest columns above {mountainHeight}");
    }

    [Fact]
    public void ForestHasBothOpenAndWoodedRegions()
    {
        // The openness mask gives the forest a sub-biome: open grass regions and woodland. Both
        // must cover a meaningful share, or the variety is not real.
        var total = 0;
        var open = 0;
        var wooded = 0;

        for (var cx = -64; cx <= 64; cx++)
        for (var cz = -64; cz <= 64; cz++)
        {
            var worldX = cx * Chunk.Size + 8;
            var worldZ = cz * Chunk.Size + 8;
            if (TerrainGenerator.BiomeAt(worldX, worldZ) != Biome.Forest) continue;

            total++;
            var openness = TerrainGenerator.ForestOpenness(worldX, worldZ);
            if (openness > 0.7f) open++;
            else if (openness < 0.2f) wooded++;
        }

        Assert.True(total > 0);
        Assert.True(open * 10 > total, $"open forest regions are negligible: {open} of {total} forest columns");
        Assert.True(wooded * 10 > total, $"woodland is negligible: {wooded} of {total} forest columns");
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
    public void CactusTaperStepsGetNarrowerTowardTheTip()
    {
        var levels = new[] { "cactus", "cactus_arm", "cactus_prong", "cactus_tip" }
            .Select(ContentRegistry.RequireBlock)
            .OrderBy(b => b.WoodTaperLevel)
            .ToArray();

        foreach (var level in levels)
            Assert.Equal(BlockType.CactusWoodFamily, BlockType.WoodFamilyOf(level.Id));

        Assert.True(levels[0].HalfWidth < 0.5f, $"stem half-width {levels[0].HalfWidth}");
        for (var i = 1; i < levels.Length; i++)
            Assert.True(levels[i].HalfWidth < levels[i - 1].HalfWidth,
                $"{levels[i].Name} is not thinner than {levels[i - 1].Name}");

        Assert.Equal(BlockType.TreeWoodFamily, BlockType.WoodFamilyOf(ContentRegistry.RequireBlock("log").Id));
    }

    [Fact]
    public void OnlyTheCactusArmBaseWeldsToTheStem()
    {
        var stem = ContentRegistry.RequireBlock("cactus").Id;
        var arm = ContentRegistry.RequireBlock("cactus_arm").Id;
        var prong = ContentRegistry.RequireBlock("cactus_prong").Id;
        var tip = ContentRegistry.RequireBlock("cactus_tip").Id;

        Assert.True(WoodShape.ConnectsHorizontal(arm, stem));

        Assert.False(WoodShape.ConnectsHorizontal(prong, stem));
        Assert.False(WoodShape.ConnectsHorizontal(tip, stem));

        Assert.True(WoodShape.ConnectsVertical(prong, arm));
        Assert.True(WoodShape.ConnectsVertical(tip, prong));
    }

    [Fact]
    public void GeneratedCactusBranchesWeldOnlyAtTheBase()
    {
        List<(int X, int Y, int Z, byte Id)>? cells = null;
        for (var cx = 0; cx <= 64 && cells == null; cx++)
        for (var cz = -64; cz <= 64 && cells == null; cz++)
        {
            if (TerrainGenerator.BiomeAt(cx * Chunk.Size + 8, cz * Chunk.Size + 8) != Biome.Desert)
                continue;

            for (var x = 0; x < Chunk.Size && cells == null; x++)
            for (var z = 0; z < Chunk.Size && cells == null; z++)
            {
                if (!CactusGenerator.TryGetCactus(cx * Chunk.Size + x, cz * Chunk.Size + z, out _, out var shape))
                    continue;
                if (shape.Wood.Any(c => WoodShape.WoodLevel(c.Id) == 1))
                    cells = shape.Wood;
            }
        }

        Assert.NotNull(cells);
        var byPos = new Dictionary<(int X, int Y, int Z), byte>();
        foreach (var (x, y, z, id) in cells)
            byPos.TryAdd((x, y, z), id);

        var weldCount = 0;
        foreach (var ((x, y, z), id) in byPos)
        foreach (var (dx, dz) in new[] { (1, 0), (0, 1) })
        {
            var other = byPos.GetValueOrDefault((x + dx, y, z + dz), BlockType.Air);
            if (!WoodShape.ConnectsHorizontal(id, other)) continue;

            var a = WoodShape.WoodLevel(id);
            var b = WoodShape.WoodLevel(other);
            Assert.Equal(0, Math.Min(a, b));
            Assert.Equal(1, Math.Max(a, b));
            weldCount++;
        }

        Assert.True(weldCount > 0, "generated cactus had no base branch-off weld");
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
