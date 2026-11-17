using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Lod;
using VoxelEngine.WorldGen.Features;
using VoxelEngine.WorldGen.Features.Underground;
using VoxelEngine.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen;

/// <summary>Bounded final-column controls for Dynamic shelves and open canyons.</summary>
[Collection("Worldgen seed")]
public sealed class S2aTerrainTests(ITestOutputHelper output)
{
    private const int CanyonX = -4000;
    private const int CanyonZ = 5000;

    [Fact]
    public void ActualOasisAndSpringPondsKeepTheirSavedColumnFootprints()
    {
        TestContent.EnsureLoaded();
        var probes = 0;
        try
        {
            WorldSeed.Apply(WorldSeed.Default, WorldGenProfile.Dynamic);
            const int oasisX = 2285;
            const int oasisZ = 1926;
            Assert.True(OasisGenerator.TryGetDisc(oasisX, oasisZ, out _, out _, out var oasisSurface));
            Assert.True(OasisGenerator.IsPond(oasisX, oasisZ));
            var oasis = Read(oasisX, oasisZ, ref probes);
            var oasisColumn = ColumnCache.Get(oasisX >> Chunk.SizeShift, oasisZ >> Chunk.SizeShift);
            Assert.True(oasisColumn.Oasis[((oasisX & Chunk.SizeMask) << Chunk.SizeShift) | (oasisZ & Chunk.SizeMask)]);
            Assert.Equal(oasisSurface, oasis.Water);
            Assert.True(oasis.Height < oasis.Water);

            var ring = Read(oasisX + WorldFeatureConfig.OasisPondRadius + 1, oasisZ, ref probes);
            Assert.True(OasisGenerator.IsDisc(oasisX + WorldFeatureConfig.OasisPondRadius + 1, oasisZ));
            Assert.False(OasisGenerator.IsPond(oasisX + WorldFeatureConfig.OasisPondRadius + 1, oasisZ));
            Assert.Equal(TerrainGenerator.NoWater, ring.Water);

            const int springX = -1533;
            const int springZ = 1597;
            Assert.True(HotSpringGenerator.TryGetSite(springX, springZ, out var springLevel));
            var spring = Read(springX, springZ, ref probes);
            Assert.Equal(springLevel, spring.Water);
            Assert.True(spring.Height < spring.Water);
            output.WriteLine($"oasis ({oasisX},{oasisZ}) floor {oasis.Height}, water {oasis.Water}, ring {ring.Height}/{ring.Water}; spring ({springX},{springZ}) floor {spring.Height}, water {spring.Water}");
        }
        finally
        {
            output.WriteLine($"S2 owner direct final-column probes: {probes}");
            WorldSeed.Apply(WorldSeed.Default);
        }
    }

    [Fact]
    public void DynamicContainsADeepDryOpenHeightmapCut()
    {
        TestContent.EnsureLoaded();
        var probes = 0;
        var greatest = 0;
        (int X, int Z) site = default;
        try
        {
            WorldSeed.Apply(WorldSeed.Default, WorldGenProfile.Dynamic);
            for (var x = -6000; x <= 6000; x += 500)
            for (var z = -6000; z <= 6000; z += 500)
            {
                probes++;
                var column = ColumnCache.Get(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
                var local = ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);
                var depth = column.Bed[local] - column.Heights[local];
                if (column.Heights[local] <= WorldGenerationConfig.SeaLevel + 3
                    || column.WaterLevel[local] >= column.Heights[local]) continue;
                if (depth <= greatest) continue;
                greatest = depth;
                site = (x, z);
            }
            Assert.True(greatest > WorldGenerationConfig.RavineDepth,
                $"no dry final-column cut deeper than the pre-S2 ravine; greatest {greatest} at {site}");
        }
        finally
        {
            output.WriteLine($"S2 direct entry probes: {probes}; greatest dry cut {greatest} at {site}");
            WorldSeed.Apply(WorldSeed.Default);
        }
    }

    [Fact]
    public void ARealDryCanyonHasAnOpenHeightmapCrossSection()
    {
        TestContent.EnsureLoaded();
        var probes = 0;
        try
        {
            WorldSeed.Apply(WorldSeed.Default, WorldGenProfile.Dynamic);
            var center = Read(CanyonX, CanyonZ, ref probes);
            Assert.True(center.Bed - center.Height >= 30);
            Assert.True(center.Height >= WorldGenerationConfig.SeaLevel + 4);
            Assert.True(center.Water < center.Height);

            for (var axis = 0; axis < 2; axis++)
            {
                var rows = new List<string>();
                var heights = new Dictionary<int, int>();
                for (var d = -64; d <= 64; d += 2)
                {
                    var x = CanyonX + (axis == 0 ? d : 0);
                    var z = CanyonZ + (axis == 1 ? d : 0);
                    var row = Read(x, z, ref probes);
                    heights[d] = row.Height;
                    rows.Add($"{d}:{row.Height}/{row.Bed}/{row.Water}");
                }
                Assert.InRange(Math.Max(heights[-2], Math.Max(heights[0], heights[2]))
                    - Math.Min(heights[-2], Math.Min(heights[0], heights[2])), 0, 3);
                Assert.True(heights.Where(kv => kv.Key >= -24 && kv.Key < 0)
                        .Any(kv => kv.Value - heights[0] >= 30)
                    && heights.Where(kv => kv.Key > 0 && kv.Key <= 24)
                        .Any(kv => kv.Value - heights[0] >= 30),
                    "the dry floor must have two open, steep walls within 24 blocks in each real cross-section");
                output.WriteLine($"canyon axis {(axis == 0 ? 'X' : 'Z')} offset:height/bed/water {string.Join(' ', rows)}");
            }
            var surfaceChunk = new Chunk(null!, new Vector3(CanyonX >> Chunk.SizeShift, 6,
                CanyonZ >> Chunk.SizeShift), generateBlockData: false);
            ChunkTerrainFill.GenerateBlockData(surfaceChunk);
            for (var y = 96; y < 112; y++)
                Assert.Equal(BlockType.Air, surfaceChunk.Storage.Get(Chunk.Idx(CanyonX & Chunk.SizeMask,
                    y - 96, CanyonZ & Chunk.SizeMask)));
            var centerColumn = ColumnCache.Get(CanyonX >> Chunk.SizeShift, CanyonZ >> Chunk.SizeShift);
            var centerLocal = ((CanyonX & Chunk.SizeMask) << Chunk.SizeShift) | (CanyonZ & Chunk.SizeMask);
            Assert.Equal(0, centerColumn.DirtDepth[centerLocal]);
            var biome = BiomeClassifier.BiomeWeights(CanyonX, CanyonZ, out var ocean, out _);
            output.WriteLine($"canyon center ({CanyonX},{CanyonZ}) biome {biome}, support {TerrainCanyons.SupportAt(CanyonX, CanyonZ, biome, ocean):F3}, ridge {TerrainCanyons.RidgeAt(CanyonX, CanyonZ):F4}");
        }
        finally
        {
            output.WriteLine($"S2 direct entry probes: {probes}");
            WorldSeed.Apply(WorldSeed.Default);
        }
    }

    [Fact]
    public void DynamicShelvesMakeARealEscarpmentAndLeaveTheSpawnNeutral()
    {
        TestContent.EnsureLoaded();
        var probes = 0;
        try
        {
            WorldSeed.Apply(WorldSeed.Default, WorldGenProfile.Dynamic);
            probes++;
            Assert.Equal(0f, TerrainShelves.HeightDelta(0f, 0f, TerrainRelief.HighlandWeight(0f, 0f), 0f));
            var candidates = new List<(int X, int Z, float Delta)>();
            for (var x = -6000; x <= 6000; x += 300)
            for (var z = -6000; z <= 6000; z += 300)
            {
                probes++;
                var highland = TerrainRelief.HighlandWeight(x, z);
                var delta = TerrainShelves.HeightDelta(x, z, highland, TerrainRelief.HighlandLift(highland));
                if (delta > 0f) candidates.Add((x, z, delta));
            }
            candidates.Sort((a, b) => b.Delta.CompareTo(a.Delta));
            Assert.NotEmpty(candidates);

            var bestShelfRise = 0f;
            var bestActualRise = 0f;
            var bestScore = 0f;
            (int X, int Z, char Axis) best = default;
            foreach (var site in candidates.GetRange(0, Math.Min(8, candidates.Count)))
            for (var axis = 0; axis < 2; axis++)
            {
                var previousDelta = 0f;
                var previousHeight = 0f;
                for (var d = -80; d <= 80; d += 4)
                {
                    var x = site.X + (axis == 0 ? d : 0);
                    var z = site.Z + (axis == 1 ? d : 0);
                    probes++;
                    var highland = TerrainRelief.HighlandWeight(x, z);
                    var delta = TerrainShelves.HeightDelta(x, z, highland, TerrainRelief.HighlandLift(highland));
                    var height = TerrainGenerator.ReliefLandHeight(x, z);
                    if (d > -80)
                    {
                        var shelfRise = MathF.Abs(delta - previousDelta);
                        var actualRise = MathF.Abs(height - previousHeight);
                        var score = MathF.Min(shelfRise, actualRise);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestShelfRise = shelfRise;
                            bestActualRise = actualRise;
                            best = (x, z, axis == 0 ? 'X' : 'Z');
                        }
                    }
                    previousDelta = delta;
                    previousHeight = height;
                }
            }
            output.WriteLine($"shelf riser near {best} over 4 blocks: shelf {bestShelfRise:F1}, final relief {bestActualRise:F1}");
            var lower = Read(best.X - (best.Axis == 'X' ? 4 : 0),
                best.Z - (best.Axis == 'Z' ? 4 : 0), ref probes);
            var upper = Read(best.X, best.Z, ref probes);
            output.WriteLine($"shelf candidate lower {lower.Height}/{lower.Bed}/{lower.Water}, upper {upper.Height}/{upper.Bed}/{upper.Water}");
            Assert.True(bestShelfRise >= 4f && bestActualRise >= 4f,
                "no bounded actual relief escarpment with a multi-block shelf contribution");
        }
        finally
        {
            output.WriteLine($"S2 direct entry probes: {probes}");
            WorldSeed.Apply(WorldSeed.Default);
        }
    }

    [Fact]
    public void DynamicCanyonsKeepActualWaterFootprintsAndShoreBanksWhole()
    {
        TestContent.EnsureLoaded();
        var probes = 0;
        var wetLand = 0;
        var elevated = 0;
        var ocean = 0;
        var shore = 0;
        try
        {
            WorldSeed.Apply(WorldSeed.Default, WorldGenProfile.Dynamic);
            for (var x = -1024; x <= 1024; x += 64)
            for (var z = -1024; z <= 1024; z += 64)
            {
                var row = Read(x, z, ref probes);
                var biome = BiomeClassifier.BiomeWeights(x, z, out var oceanWeight, out _);
                if (oceanWeight >= WorldGenerationConfig.OceanShoreWeight && oceanWeight < 0.5f)
                    shore++;
                if (biome == Biome.Ocean)
                {
                    ocean++;
                    Assert.Equal(row.Bed, row.Height);
                    continue;
                }
                if (oceanWeight >= WorldGenerationConfig.OceanShoreWeight)
                {
                    Assert.Equal(row.Bed, row.Height);
                }
                if (row.Water == TerrainGenerator.NoWater) continue;
                wetLand++;
                if (row.Water > WorldGenerationConfig.SeaLevel) elevated++;
                Assert.Equal(row.Bed, row.Height);
                if (row.Bed >= row.Water)
                    Assert.True(row.Height >= row.Water,
                        $"the dry bank at ({x},{z}) opened beneath its planned level {row.Water}");
            }
            Assert.True(wetLand > 0 && ocean > 0 && shore > 0,
                $"water coverage missing: wet land {wetLand}, ocean {ocean}, shore {shore}");
            output.WriteLine($"protected wet land {wetLand}, elevated {elevated}, ocean {ocean}, shore {shore}");
        }
        finally
        {
            output.WriteLine($"S2 direct entry probes: {probes}");
            WorldSeed.Apply(WorldSeed.Default);
        }
    }

    [Fact]
    public void CanyonAndShelfColumnsRepeatAcrossSeedAndCacheResets()
    {
        TestContent.EnsureLoaded();
        var probes = 0;
        var sites = new[] { (CanyonX, CanyonZ), (CanyonX - 16, CanyonZ),
            (2100, -3608), (2096, -3608), (0, 0), (-1, -1) };
        try
        {
            foreach (var seed in new[] { WorldSeed.Default, WorldSeed.Default + 7, WorldSeed.Default + 7919 })
            {
                WorldSeed.Apply(seed, WorldGenProfile.Dynamic);
                var first = new (int Height, int Bed, int Water)[sites.Length];
                for (var i = 0; i < sites.Length; i++)
                    first[i] = Read(sites[i].Item1, sites[i].Item2, ref probes);
                WorldSeed.Apply(seed, WorldGenProfile.Legacy);
                _ = Read(0, 0, ref probes); // Prime a different profile before the quiescent reset.
                WorldSeed.Apply(seed, WorldGenProfile.Dynamic);
                for (var i = 0; i < sites.Length; i++)
                    Assert.Equal(first[i], Read(sites[i].Item1, sites[i].Item2, ref probes));
            }
        }
        finally
        {
            output.WriteLine($"S2 direct entry probes: {probes}");
            WorldSeed.Apply(WorldSeed.Default);
        }
    }

    [Fact]
    public void CanyonLodCapsStayInsideTheGeneratedHeightmapAndIndexBudget()
    {
        TestContent.EnsureLoaded();
        var probes = 0;
        try
        {
            WorldSeed.Apply(WorldSeed.Default, WorldGenProfile.Dynamic);
            for (var level = 0; level < LodRegion.Levels.Length; level++)
            {
                var spec = LodRegion.Levels[level];
                var rx = (int)MathF.Floor(CanyonX / (float)spec.RegionBlocks);
                var rz = (int)MathF.Floor(CanyonZ / (float)spec.RegionBlocks);
                var mesh = LodRegion.BuildMeshData(level, rx, rz);
                Assert.NotEmpty(mesh.Vertices);
                Assert.True(mesh.Vertices.Count <= ushort.MaxValue - 7);
                var checkedTops = 0;
                var stride = Math.Max(1, mesh.Vertices.Count / 64);
                for (var q = 0; q < mesh.Vertices.Count; q += 4 * stride)
                {
                    var v = mesh.Vertices[q];
                    if ((v.Normal & 7) != 4 || v.PackedColor >> 24 == 253) continue;
                    var vx = rx * spec.RegionBlocks + (int)v.Position.X;
                    var vz = rz * spec.RegionBlocks + (int)v.Position.Z;
                    var ground = int.MaxValue;
                    var wet = false;
                    for (var x = vx - 1; x <= vx; x++)
                    for (var z = vz - 1; z <= vz; z++)
                    {
                        var row = Read(x, z, ref probes);
                        wet |= row.Water > row.Height;
                        ground = Math.Min(ground, row.Height);
                    }
                    if (wet) continue;
                    Assert.True(v.Position.Y <= ground, $"LOD {level} at ({vx},{vz}) rose above {ground}");
                    checkedTops++;
                }
                Assert.True(checkedTops > 0);
                output.WriteLine($"LOD {level} region ({rx},{rz}): vertices {mesh.Vertices.Count}, indices {mesh.Indices.Count}, vertex headroom {ushort.MaxValue - 7 - mesh.Vertices.Count}");
            }
        }
        finally
        {
            output.WriteLine($"S2 direct entry probes: {probes}; mesh builds internally read dependent columns");
            WorldSeed.Apply(WorldSeed.Default);
        }
    }

    private static (int Height, int Bed, int Water) Read(int x, int z, ref int probes)
    {
        probes++;
        var column = ColumnCache.Get(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
        var local = ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);
        return (column.Heights[local], column.Bed[local], column.WaterLevel[local]);
    }
}
