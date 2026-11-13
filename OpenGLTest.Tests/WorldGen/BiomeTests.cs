using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Lod;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using VoxelEngine.WorldGen.Trees;
using Xunit;

namespace VoxelEngine.Tests.WorldGen;

/// <summary>
/// Covers the biome split: the 3x3 climate grid over continentalness, temperature and the two
/// humidity ramps (forest needs wet air, so the dry axis reads desert, steppe, tundra), the smooth
/// weights the heights lerp by, the ragged desert border, the ocean depth ramp and its floor bands,
/// the desert sand/sandstone column and which trees each biome grows. Classification scans read
/// the undithered biome (<see cref="TerrainGenerator.BiomeAt"/>); only the dither test reads the
/// dithered column biomes. The near-terrain and far-LOD views are checked separately.
/// </summary>
public sealed class BiomeTests
{
    public BiomeTests() => TestContent.EnsureLoaded();

    /// <summary>The surface chunk of a world column and the height's local Y inside it.</summary>
    private static (Chunk Chunk, int Ly) SurfaceChunk(int x, int z, out int height)
    {
        height = ColumnCache.GetHeights(x >> Chunk.SizeShift, z >> Chunk.SizeShift)
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
    public void SteppeAndSavannaBothExistNearOrigin()
    {
        var sawSteppe = false;
        var sawSavanna = false;
        const int radius = 64;
        for (var cx = -radius; cx <= radius && !(sawSteppe && sawSavanna); cx++)
        for (var cz = -radius; cz <= radius && !(sawSteppe && sawSavanna); cz++)
        {
            var biome = TerrainGenerator.BiomeAt(cx * Chunk.Size + 8, cz * Chunk.Size + 8);
            sawSteppe |= biome == Biome.Steppe;
            sawSavanna |= biome == Biome.Savanna;
        }

        Assert.True(sawSteppe, "no steppe biome within 64 chunks");
        Assert.True(sawSavanna, "no savanna biome within 64 chunks");
    }

    [Fact]
    public void JungleExistsNearSpawnButNotAtIt()
    {
        var count = 0;
        var total = 0;
        const int radius = 96;
        for (var cx = -radius; cx <= radius; cx += 2)
        for (var cz = -radius; cz <= radius; cz += 2)
        {
            total++;
            if (TerrainGenerator.BiomeAt(cx * Chunk.Size + 8, cz * Chunk.Size + 8) == Biome.Jungle) count++;
        }

        Assert.True(count > 0, "no jungle within 96 chunks");
        Assert.True(count < total / 4, $"jungle covers {count} of {total} samples: too common");
        Assert.NotEqual(Biome.Jungle, TerrainGenerator.BiomeAt(0, 0));
    }

    [Fact]
    public void JungleOnlyClassifiesInsideTheWarmWetCorner()
    {
        for (var ti = 0; ti <= 400; ti++)
        for (var hi = 0; hi <= 400; hi++)
        {
            var t = -1f + ti * 0.005f;
            var h = -1f + hi * 0.005f;
            var warm = BiomeClassifier.WarmOf(t);
            var dry = BiomeClassifier.DryOf(h);
            var semiDry = BiomeClassifier.SemiDryOf(h);
            var jungle = BiomeClassifier.JungleOf(t, h);
            var biome = BiomeClassifier.Classify(0f, BiomeClassifier.ColdOf(t), warm, dry, semiDry, jungle);
            if (biome != Biome.Jungle) continue;
            Assert.True(warm >= 0.5f && semiDry < 0.5f && dry < 0.5f,
                $"t {t} h {h} is jungle outside the warm, wet corner");
        }
    }

    [Fact]
    public void HumidityRampsAreExclusive()
    {
        // The semi-dry ramp lies wholly on the wet side of the dry ramp: wherever any dryness is,
        // the air is fully semi-dry, so the savanna and the steppe always sit between forest and
        // the dry biomes. Swept through both ramps' real constants.
        var sawDry = false;
        var sawSemiDryRamp = false;
        for (var i = 0; i <= 4000; i++)
        {
            var h = -1f + i * 0.0005f;
            var dry = BiomeClassifier.DryOf(h);
            var semiDry = BiomeClassifier.SemiDryOf(h);
            if (dry > 0f)
            {
                sawDry = true;
                Assert.True(semiDry == 1f, $"humidity {h} is dry ({dry}) but only {semiDry} semi-dry");
            }

            sawSemiDryRamp |= semiDry > 0f && semiDry < 1f;
        }

        Assert.True(sawDry, "no humidity is dry");
        Assert.True(sawSemiDryRamp, "the sweep never crossed the semi-dry ramp");
    }

    [Fact]
    public void TheDryAxisNeverClassifiesForest()
    {
        // Over a fine (temperature, humidity) lattice through the real seams: dry air is tundra,
        // steppe or desert, never forest; forest needs wet air; savanna is warm and short of dry;
        // and desert is still exactly min(warm, dry) >= 0.5, the line the dune blend keys on.
        var seen = new HashSet<Biome>();
        for (var ti = 0; ti <= 400; ti++)
        for (var hi = 0; hi <= 400; hi++)
        {
            var t = -1f + ti * 0.005f;
            var h = -1f + hi * 0.005f;
            var cold = BiomeClassifier.ColdOf(t);
            var warm = BiomeClassifier.WarmOf(t);
            var dry = BiomeClassifier.DryOf(h);
            var semiDry = BiomeClassifier.SemiDryOf(h);
            var biome = BiomeClassifier.Classify(0f, cold, warm, dry, semiDry);
            seen.Add(biome);

            if (dry >= 0.5f)
                Assert.True(biome is Biome.Frozen or Biome.Steppe or Biome.Desert,
                    $"t {t} h {h} is dry ({dry}) but classifies {biome}");
            if (biome == Biome.Forest)
                Assert.True(semiDry < 0.5f, $"t {t} h {h} is forest with semi-dry {semiDry}");
            if (biome == Biome.Savanna)
                Assert.True(warm >= 0.5f && dry < 0.5f, $"t {t} h {h} is savanna with warm {warm}, dry {dry}");
            Assert.Equal(MathF.Min(warm, dry) >= 0.5f, biome == Biome.Desert);
        }

        foreach (var biome in new[] { Biome.Forest, Biome.Steppe, Biome.Savanna, Biome.Desert, Biome.Taiga, Biome.Frozen })
            Assert.True(seen.Contains(biome), $"the climate lattice never classifies {biome}");
    }

    [Fact]
    public void AridWeightIsSmooth()
    {
        // aridWeight = SemiDryOf(h) * (1 - ocean), so by the product rule a unit step can move it by
        // at most the semi-dry smoothstep's steepest slope times the humidity change (scaled by the
        // land share) plus the semi-dry share times the ocean change. Holding that bound on every
        // stride-1 step, coasts included, is what keeps the hills LandHeight flattens with it a
        // slope rather than a cliff.
        var k = 1.5f / (WorldGenerationConfig.HumiditySemiDryEnd - WorldGenerationConfig.HumiditySemiDryStart);
        var moved = 0;
        var coastal = 0;
        for (var z = -2048; z <= 2048; z += 256)
        {
            BiomeClassifier.SampleClimate(-2048, z, out _, out var h0);
            BiomeClassifier.BiomeWeights(-2048, z, out var ocean0, out _, out var arid0);
            for (var x = -2047; x <= 2048; x++)
            {
                BiomeClassifier.SampleClimate(x, z, out _, out var h1);
                BiomeClassifier.BiomeWeights(x, z, out var ocean1, out _, out var arid1);

                var dArid = MathF.Abs(arid1 - arid0);
                var dOcean = MathF.Abs(ocean1 - ocean0);
                var bound = k * MathF.Abs(h1 - h0) * (1f - MathF.Min(ocean0, ocean1))
                            + MathF.Max(BiomeClassifier.SemiDryOf(h0), BiomeClassifier.SemiDryOf(h1)) * dOcean
                            + 1e-5f;
                Assert.True(dArid <= bound,
                    $"aridWeight jumps {dArid} between x {x - 1} and {x} at z {z}, over the bound {bound}");
                if (dArid > 0f) moved++;
                if (dOcean > 0f) coastal++;

                h0 = h1;
                ocean0 = ocean1;
                arid0 = arid1;
            }
        }

        Assert.True(moved > 0, "aridWeight never changed along the scanned rows");
        Assert.True(coastal > 0, "no scanned step crossed a coast");
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
        var hematiteOre = ContentRegistry.RequireBlock("hematite_ore").Id;
        var tinOre = ContentRegistry.RequireBlock("tin_ore").Id;

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
                Assert.True(id == expected || (expected == rock && (id == ore || id == coalOre || id == hematiteOre || id == tinOre)),
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
            var column = ColumnCache.Get(cx, cz);
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
            var column = ColumnCache.Get(cx, cz);
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

            var column = ColumnCache.Get(cx, cz);
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
            var column = ColumnCache.Get(cx, cz);
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
    public void EveryTreeGrowsInItsSpeciesBiome()
    {
        // Whatever roots in a land chunk is a species whose worldgen rules name the column's biome,
        // so no forest tree takes root in the sand and no desert species grows in the forest. Every
        // land biome some species is planted in (an oasis species counts only through its oases)
        // and whose chunk centers the scan meets must produce a tree, so no biome's check is vacuous.
        var required = new HashSet<Biome>();
        foreach (var species in TreeSpeciesRegistry.All)
        {
            if (species.Worldgen is not { Oasis: false } worldgen) continue;
            foreach (var biome in Enum.GetValues<Biome>())
                if (biome != Biome.Ocean && worldgen.GrowsIn(biome))
                    required.Add(biome);
        }

        Assert.NotEmpty(required);
        var seen = new HashSet<Biome>();
        var centers = new HashSet<Biome>();

        // Chebyshev rings outward, so the scan stops early once every biome has produced a tree;
        // a sparse biome keeps it going to the edge of the window, widened only if still unseen.
        bool Done() => required.IsSubsetOf(seen);
        bool CenterBiomesSeen() => required.Where(centers.Contains).All(seen.Contains);
        void ScanChunk(int cx, int cz)
        {
            var center = TerrainGenerator.BiomeAt(cx * Chunk.Size + 8, cz * Chunk.Size + 8);
            if (center == Biome.Ocean) return;
            centers.Add(center);

            for (var x = 0; x < Chunk.Size; x++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var worldX = cx * Chunk.Size + x;
                var worldZ = cz * Chunk.Size + z;
                if (!TreeGenerator.TryGetTree(worldX, worldZ, out var species, out _))
                    continue;
                var biome = TerrainGenerator.BiomeAt(worldX, worldZ);
                Assert.True(species.Worldgen!.GrowsIn(biome), $"{species.Name} rooted in {biome} at ({worldX}, {worldZ})");
                seen.Add(biome);
            }
        }

        const int window = 64;
        const int widened = 96;
        for (var ring = 0; ring <= widened && !Done(); ring++)
        {
            if (ring > window && CenterBiomesSeen()) break;
            for (var cx = -ring; cx <= ring && !Done(); cx++)
            for (var cz = -ring; cz <= ring && !Done(); cz++)
                if (Math.Max(Math.Abs(cx), Math.Abs(cz)) == ring)
                    ScanChunk(cx, cz);
        }

        foreach (var biome in required.Where(centers.Contains))
            Assert.True(seen.Contains(biome), $"no tree rooted in {biome}, though the scan met its chunks");
    }

    [Fact]
    public void DesertsGrowALeaflessSpecies()
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
                var wx = cx * Chunk.Size + x;
                var wz = cz * Chunk.Size + z;
                if (TerrainGenerator.BiomeAt(wx, wz) != Biome.Desert
                    || !TreeGenerator.TryGetTree(wx, wz, out var species, out _))
                    continue;

                Assert.False(species.HasLeaves, $"the desert grew the leafy {species.Name}");
                var shape = TreeBatches.ShapeOf(wx, wz);
                Assert.True(shape.WoodCount > 0);
                Assert.Equal(shape.WoodCount, shape.Cells.Length);
                return;
            }
        }

        Assert.Fail("no tree found in 24 desert chunks");
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
            var column = ColumnCache.Get(cx, cz);
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

            var column = ColumnCache.Get(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
            var local = ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);
            Assert.Equal(0f, column.EntranceWeight[local]);
            oceanColumns++;
        }

        Assert.True(oceanColumns > 0, "no ocean columns found in the scanned area");
    }

    [Fact]
    public void BiomeBorderDitherFlipsCoherentPatchesNotSingleColumns()
    {
        // Inside the band around the desert line the stored (dithered) column biome may differ from
        // the undithered one, in coherent patches. A desert column that flips takes the grass biome
        // across the line it borders: steppe where warmth binds min(warm, dry), savanna where
        // dryness does. Forest never borders the desert, so no flip ever gives forest.
        var band = 0;
        var differingNeighbor = 0;
        var desertFlips = 0;
        const int step = 4;

        for (var x = -2048; x < 2048; x += step)
        for (var z = -2048; z < 2048; z += step)
        {
            var undithered = TerrainGenerator.BiomeWeights(x, z, out _, out var desertWeight);
            if (MathF.Abs(desertWeight - 0.5f) >= WorldGenerationConfig.BiomeBorderDither) continue;

            var column = ColumnCache.Get(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
            var local = ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);
            var here = column.Biomes[local];
            if (here != undithered)
            {
                Assert.NotEqual(Biome.Forest, here);
                if (undithered == Biome.Desert)
                {
                    BiomeClassifier.SampleClimate(x, z, out var temperature, out var humidity);
                    var across = BiomeClassifier.WarmOf(temperature) <= BiomeClassifier.DryOf(humidity)
                        ? Biome.Steppe
                        : Biome.Savanna;
                    Assert.True(here == across, $"desert at ({x},{z}) dithered to {here}, but it borders {across}");
                    desertFlips++;
                }
            }

            if (here is not (Biome.Desert or Biome.Savanna or Biome.Steppe)) continue;
            band++;

            var nx = x + step;
            var neighbor = ColumnCache.Get(nx >> Chunk.SizeShift, z >> Chunk.SizeShift);
            var neighborLocal = ((nx & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);
            if (neighbor.Biomes[neighborLocal] != here) differingNeighbor++;
        }

        Assert.True(band > 0, "no desert-border dither band found in the scanned area");
        Assert.True(desertFlips > 0, "no desert column dithered across its border");
        Assert.True(differingNeighbor * 3 < band,
            $"the dither flips single columns: {differingNeighbor} of {band} samples differ from a neighbor");
    }

    [Fact]
    public void TemperatureRampsAreExclusiveWithATemperateBandBetween()
    {
        // Sweeps raw temperature through the real ramps and the real classifier. No column is ever
        // both cold and warm, the cold biomes and the (fully dry) desert each own one end of the
        // axis, and everything strictly between them is temperate: forest in wet air and steppe in
        // semi-dry or dry air, never a warm or a cold biome. Humidity is swept across both ramps.
        const int samples = 400;
        const int humiditySamples = 200;
        float T(int i) => -1f + i * 0.005f;
        float H(int j) => -1f + j * 0.01f;

        float? tColdMax = null;
        float? tHotMin = null;
        for (var i = 0; i <= samples; i++)
        {
            var t = T(i);
            var cold = BiomeClassifier.ColdOf(t);
            var warm = BiomeClassifier.WarmOf(t);
            Assert.False(cold > 0f && warm > 0f, $"t {t} is both cold ({cold}) and warm ({warm})");

            for (var j = 0; j <= humiditySamples; j++)
                if (BiomeClassifier.Classify(0f, cold, warm, BiomeClassifier.DryOf(H(j)),
                        BiomeClassifier.SemiDryOf(H(j))) is Biome.Taiga or Biome.Frozen)
                    tColdMax = t;
            if (tHotMin == null && BiomeClassifier.Classify(0f, cold, warm, 1f, 1f) == Biome.Desert)
                tHotMin = t;
        }

        Assert.True(tColdMax.HasValue, "no temperature classifies taiga or frozen");
        Assert.True(tHotMin.HasValue, "no temperature classifies desert even when fully dry");
        Assert.True(tColdMax < tHotMin, $"the cold biomes reach t {tColdMax}, past the desert's {tHotMin}");

        var sawForest = false;
        var sawSteppe = false;
        for (var i = 0; i <= samples; i++)
        {
            var t = T(i);
            var cold = BiomeClassifier.ColdOf(t);
            var warm = BiomeClassifier.WarmOf(t);
            for (var j = 0; j <= humiditySamples; j++)
            {
                var h = H(j);
                var semiDry = BiomeClassifier.SemiDryOf(h);
                var biome = BiomeClassifier.Classify(0f, cold, warm, BiomeClassifier.DryOf(h), semiDry);
                if (t < tColdMax)
                {
                    Assert.True(biome is Biome.Taiga or Biome.Frozen, $"t {t} h {h} is {biome}, below the cold line");
                }
                else if (t > tColdMax && t < tHotMin)
                {
                    var temperate = semiDry < 0.5f ? Biome.Forest : Biome.Steppe;
                    Assert.True(biome == temperate, $"t {t} h {h} is {biome} inside the temperate band, expected {temperate}");
                    sawForest |= biome == Biome.Forest;
                    sawSteppe |= biome == Biome.Steppe;
                }
            }

            if (t > tHotMin)
                Assert.Equal(Biome.Desert, BiomeClassifier.Classify(0f, cold, warm, 1f, 1f));
        }

        Assert.True(sawForest && sawSteppe, $"the temperate band never held both forest ({sawForest}) and steppe ({sawSteppe})");
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
    public void DesertAndSnowAreNeverNearAndTheDryCorridorIsSteppeOrTundra()
    {
        // The temperate band must be at least the full-detail load radius wide, so a player standing
        // on the last dune never has snow loaded around them. Hot = would be desert if fully dry (the
        // temperature band itself, independent of humidity luck); cold = the snow line. And the dry
        // corridor between them never passes a forest: every dry land column is desert, steppe or
        // frozen tundra.
        var bufferBlocks = StreamingConfig.ChunkRadius * Chunk.Size;
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
        var dryLandInWindow = 0;
        for (var gz = 0; gz < size; gz++)
        for (var gx = 0; gx < size; gx++)
        {
            var x = Coord(gx);
            var z = Coord(gz);
            var k = gz * size + gx;
            hot[k] = BiomeClassifier.Classify(0f, TerrainGenerator.ColdWeight(x, z),
                TerrainGenerator.WarmWeight(x, z), 1f, 1f) == Biome.Desert;
            if (InWindow(gx) && InWindow(gz))
            {
                if (hot[k]) hotInWindow++;
                BiomeClassifier.SampleClimate(x, z, out _, out var humidity);
                var biome = TerrainGenerator.BiomeAt(x, z);
                if (biome != Biome.Ocean && BiomeClassifier.DryOf(humidity) >= 0.5f)
                {
                    dryLandInWindow++;
                    Assert.True(biome is Biome.Desert or Biome.Steppe or Biome.Frozen,
                        $"dry land at ({x},{z}) classifies {biome}");
                }
            }

            if (SnowGenerator.DepthAt(x, z, 0) <= 0) continue;
            seed[k] = k;
            frontier.Add(k);
        }

        Assert.True(frontier.Count > 0, "no snow found in the scanned area");
        Assert.True(hotInWindow > 0, "no hot column found in the asserted window");
        Assert.True(dryLandInWindow > 0, "no dry land column found in the asserted window");

        // Multi-source 8-connected BFS from the snow. Ring r is a Chebyshev distance of r * stride,
        // a lower bound on the Euclidean band, so a failure measured on the diagonal is a real
        // shortfall rather than a test artifact. The rule is "no hot cell closer than bufferBlocks",
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
