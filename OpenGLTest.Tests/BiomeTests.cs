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
        // The ore passes replace deep stone at any depth, so a stone-band floor may read as ore
        var ore = ContentRegistry.RequireBlock("malachite_ore").Id;
        var coalOre = ContentRegistry.RequireBlock("coal_ore").Id;

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
                Assert.True(id == expected || (expected == rock && (id == ore || id == coalOre)),
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

    private static List<Dictionary<(int X, int Y, int Z), byte>> FindStampedCacti(int max)
    {
        var cacti = new List<Dictionary<(int X, int Y, int Z), byte>>();
        for (var cx = 0; cx <= 64 && cacti.Count < max; cx++)
        for (var cz = -64; cz <= 64 && cacti.Count < max; cz++)
        {
            if (TerrainGenerator.BiomeAt(cx * Chunk.Size + 8, cz * Chunk.Size + 8) != Biome.Desert)
                continue;

            for (var x = 0; x < Chunk.Size && cacti.Count < max; x++)
            for (var z = 0; z < Chunk.Size && cacti.Count < max; z++)
            {
                if (!CactusGenerator.TryGetCactus(cx * Chunk.Size + x, cz * Chunk.Size + z, out _, out var shape))
                    continue;
                if (!shape.Wood.Any(c => WoodShape.WoodLevel(c.Id) == 1))
                    continue;

                var stamped = new Dictionary<(int X, int Y, int Z), byte>();
                foreach (var (wx, wy, wz, id) in shape.Wood)
                    stamped.TryAdd((wx, wy, wz), id);
                cacti.Add(stamped);
            }
        }
        return cacti;
    }

    [Fact]
    public void GeneratedCactusBranchesWeldOnlyAtTheBase()
    {
        var cacti = FindStampedCacti(8);
        Assert.NotEmpty(cacti);

        var weldCount = 0;
        foreach (var cells in cacti)
        foreach (var ((x, y, z), id) in cells)
        foreach (var (dx, dz) in new[] { (1, 0), (0, 1) })
        {
            var other = cells.GetValueOrDefault((x + dx, y, z + dz), BlockType.Air);
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
    public void GeneratedCactusNarrowsMonotonicallyUpward()
    {
        var cacti = FindStampedCacti(8);
        Assert.NotEmpty(cacti);

        foreach (var cells in cacti)
        foreach (var column in cells.GroupBy(c => (c.Key.X, c.Key.Z)))
        {
            var widths = column.OrderBy(c => c.Key.Y).Select(c => BlockType.ById[c.Value]!.HalfWidth).ToArray();
            for (var i = 1; i < widths.Length; i++)
                Assert.True(widths[i] <= widths[i - 1],
                    $"cactus column widened from {widths[i - 1]} to {widths[i]} climbing upward");
        }
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

    [Fact]
    public void OceanShoreBandSlopesBelowTheWaterlineInsteadOfStepping()
    {
        // The old hard ocean classification clamped every land column to sea level + 1, so the
        // coast met the water as a step. The blended band must now be ocean biome and may sit
        // below the waterline (which is what makes it a slope).
        var wetBand = 0;
        for (var cx = -64; cx <= 64 && wetBand < 8; cx++)
        for (var cz = -64; cz <= 64 && wetBand < 8; cz++)
        {
            var column = Chunk.GetColumnData(cx, cz);
            for (var i = 0; i < column.Heights.Length && wetBand < 8; i++)
            {
                var x = cx * Chunk.Size + (i >> Chunk.SizeShift);
                var z = cz * Chunk.Size + (i & Chunk.SizeMask);
                TerrainGenerator.BiomeWeights(x, z, out var oceanWeight, out _);
                if (oceanWeight < WorldGenerationConfig.OceanShoreWeight || oceanWeight >= 0.5f) continue;

                Assert.Equal(Biome.Ocean, TerrainGenerator.BiomeAt(x, z));
                if (column.Bed[i] >= WorldGenerationConfig.SeaLevel) continue;

                Assert.True(TerrainGenerator.IsWaterColumn(x, z, column.Bed[i]));
                wetBand++;
            }
        }

        Assert.True(wetBand > 0, "the blended shore band never dips below the waterline");
    }

    [Fact]
    public void OceanShoreHasNoSurfaceCaveEntrances()
    {
        // An entrance on a beach let a cave breach the sand into a water-filled hole.
        var oceanColumns = 0;
        for (var x = -1024; x <= 1024; x += 8)
        for (var z = -1024; z <= 1024; z += 8)
        {
            TerrainGenerator.BiomeWeights(x, z, out var oceanWeight, out _);
            if (oceanWeight < WorldGenerationConfig.OceanShoreWeight) continue;

            var column = Chunk.GetColumnData(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
            var local = ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);
            Assert.Equal(0f, column.EntranceWeight[local]);
            oceanColumns++;
        }

        Assert.True(oceanColumns > 0, "no ocean columns found in the scanned area");
    }

    [Fact]
    public void BiomeBorderDitherFlipsCoherentPatchesNotSingleColumns()
    {
        var band = 0;
        var differingNeighbour = 0;
        const int step = 4;

        for (var x = -2048; x < 2048; x += step)
        for (var z = -2048; z < 2048; z += step)
        {
            TerrainGenerator.BiomeWeights(x, z, out _, out var desertWeight);
            if (MathF.Abs(desertWeight - 0.5f) >= WorldGenerationConfig.BiomeBorderDither) continue;

            var column = Chunk.GetColumnData(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
            var local = ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);
            var here = column.Biomes[local];
            if (here is not (Biome.Forest or Biome.Desert)) continue;
            band++;

            var nx = x + step;
            var neighbour = Chunk.GetColumnData(nx >> Chunk.SizeShift, z >> Chunk.SizeShift);
            var neighbourLocal = ((nx & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);
            if (neighbour.Biomes[neighbourLocal] != here) differingNeighbour++;
        }

        Assert.True(band > 0, "no forest/desert dither band found in the scanned area");
        Assert.True(differingNeighbour * 3 < band,
            $"the dither flips single columns: {differingNeighbour} of {band} samples differ from a neighbour");
    }

    [Fact]
    public void TemperatureRampsAreExclusiveWithATemperateBandBetween()
    {
        // Sweeps raw temperature through the real ramps and the real classifier. No column is ever
        // both cold and warm, the cold biomes and the (fully dry) desert each own one end of the
        // axis, and everything strictly between them is temperate forest whatever the humidity.
        var dries = new[] { 0f, 0.5f, 1f };
        const int samples = 400;
        float T(int i) => -1f + i * 0.005f;

        float? tColdMax = null;
        float? tHotMin = null;
        for (var i = 0; i <= samples; i++)
        {
            var t = T(i);
            var cold = TerrainGenerator.ColdOf(t);
            var warm = TerrainGenerator.WarmOf(t);
            Assert.False(cold > 0f && warm > 0f, $"t {t} is both cold ({cold}) and warm ({warm})");

            foreach (var dry in dries)
                if (TerrainGenerator.Classify(0f, cold, warm, dry) is Biome.Taiga or Biome.Frozen)
                    tColdMax = t;
            if (tHotMin == null && TerrainGenerator.Classify(0f, cold, warm, 1f) == Biome.Desert)
                tHotMin = t;
        }

        Assert.True(tColdMax.HasValue, "no temperature classifies taiga or frozen");
        Assert.True(tHotMin.HasValue, "no temperature classifies desert even when fully dry");
        Assert.True(tColdMax < tHotMin, $"the cold biomes reach t {tColdMax}, past the desert's {tHotMin}");

        for (var i = 0; i <= samples; i++)
        {
            var t = T(i);
            var cold = TerrainGenerator.ColdOf(t);
            var warm = TerrainGenerator.WarmOf(t);
            foreach (var dry in dries)
            {
                var biome = TerrainGenerator.Classify(0f, cold, warm, dry);
                if (t < tColdMax)
                    Assert.True(biome is Biome.Taiga or Biome.Frozen, $"t {t} dry {dry} is {biome}, below the cold line");
                else if (t > tColdMax && t < tHotMin)
                    Assert.True(biome == Biome.Forest, $"t {t} dry {dry} is {biome} inside the temperate band");
            }

            if (t > tHotMin)
                Assert.Equal(Biome.Desert, TerrainGenerator.Classify(0f, cold, warm, 1f));
        }
    }

    [Fact]
    public void SurfaceWaterFadeHasClosedOnEveryDesertColumn()
    {
        // The surface-water gate fades over desertWeight FadeStart..FadeEnd, so it must have fully
        // closed by the time a column classifies desert, on the humid border and the temperate
        // border alike. Inland only: the shore band scales desertWeight by the ocean's share.
        var inlandDesert = 0;
        for (var x = -2048; x <= 2048; x += 8)
        for (var z = -2048; z <= 2048; z += 8)
        {
            var biome = TerrainGenerator.BiomeWeights(x, z, out var ocean, out var desertWeight);
            if (biome != Biome.Desert || ocean != 0f) continue;
            inlandDesert++;
            Assert.True(desertWeight >= WorldGenerationConfig.WaterDesertFadeEnd,
                $"desert at ({x},{z}) has desertWeight {desertWeight}, below the closed water fade");
        }

        Assert.True(inlandDesert > 0, "no inland desert column found in the scanned area");
    }

    [Fact]
    public void HotAirKeepsAFullLoadRadiusFromSnow()
    {
        // The temperate band must be at least the full-detail load radius wide, so a player standing
        // on the last dune never has snow loaded around them. Hot = would be desert if fully dry (the
        // temperature band itself, independent of humidity luck); cold = the snow line.
        var bufferBlocks = WorldGenerationConfig.ChunkRadius * Chunk.Size;
        const int stride = 8;
        const int window = 2048;
        // Cold seeds just outside the asserted window still count.
        var reach = window + bufferBlocks;
        var size = reach * 2 / stride + 1;
        int Coord(int g) => g * stride - reach;
        bool InWindow(int g) => Math.Abs(Coord(g)) <= window;

        var hot = new bool[size * size];
        var seed = new int[size * size];
        Array.Fill(seed, -1);
        var frontier = new List<int>();
        var hotInWindow = 0;
        for (var gz = 0; gz < size; gz++)
        for (var gx = 0; gx < size; gx++)
        {
            var x = Coord(gx);
            var z = Coord(gz);
            var k = gz * size + gx;
            hot[k] = TerrainGenerator.Classify(0f, TerrainGenerator.ColdWeight(x, z),
                TerrainGenerator.WarmWeight(x, z), 1f) == Biome.Desert;
            if (hot[k] && InWindow(gx) && InWindow(gz)) hotInWindow++;
            if (SnowGenerator.DepthAt(x, z) <= 0) continue;
            seed[k] = k;
            frontier.Add(k);
        }

        Assert.True(frontier.Count > 0, "no snow found in the scanned area");
        Assert.True(hotInWindow > 0, "no hot column found in the asserted window");

        // Multi-source 8-connected BFS from the snow. Ring r is a Chebyshev distance of r * stride,
        // a lower bound on the Euclidean band, so a failure measured on the diagonal is a real
        // shortfall rather than a test artefact. The rule is "no hot cell closer than bufferBlocks",
        // so rings stop before reaching it and a hot cell exactly bufferBlocks away is never visited.
        void Check(int k, int ring)
        {
            int gx = k % size, gz = k / size;
            if (!hot[k] || !InWindow(gx) || !InWindow(gz)) return;
            var s = seed[k];
            Assert.Fail($"hot column ({Coord(gx)},{Coord(gz)}) is {ring * stride} blocks (Chebyshev) from " +
                        $"snow at ({Coord(s % size)},{Coord(s / size)}), under the {bufferBlocks}-block load radius");
        }

        foreach (var k in frontier) Check(k, 0);
        for (var ring = 1; ring * stride < bufferBlocks && frontier.Count > 0; ring++)
        {
            var next = new List<int>();
            foreach (var k in frontier)
            {
                int gx = k % size, gz = k / size;
                for (var dz = -1; dz <= 1; dz++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    int nx = gx + dx, nz = gz + dz;
                    if ((uint)nx >= (uint)size || (uint)nz >= (uint)size) continue;
                    var n = nz * size + nx;
                    if (seed[n] >= 0) continue;
                    seed[n] = seed[k];
                    next.Add(n);
                }
            }

            foreach (var n in next) Check(n, ring);
            frontier = next;
        }
    }
}
