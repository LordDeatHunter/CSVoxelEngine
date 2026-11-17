using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Lod;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using VoxelEngine.WorldGen.Structures;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen;

/// <summary>Bounded behavioral checks of the Dynamic range, valley, knoll and LOD stage.</summary>
[Collection("Worldgen seed")]
public sealed class DynamicReliefTests(ITestOutputHelper output)
{
    private static readonly int[] Seeds = [WorldSeed.Default, WorldSeed.Default + 7, WorldSeed.Default + 7919];

    [Fact]
    public void DynamicHasConnectedCrestsOpenValleysAndBoundedColumns()
    {
        TestContent.EnsureLoaded();
        try
        {
            var high = 0;
            var pairs = 0;
            var low = 0;
            var maximum = float.NegativeInfinity;
            (int Seed, int X, int Z) highest = default;
            foreach (var seed in Seeds)
            {
                WorldSeed.Apply(seed, WorldGenProfile.Dynamic);
                var previous = new Dictionary<(int X, int Z), float>();
                for (var ix = 0; ix < 35; ix++)
                for (var iz = 0; iz < 35; iz++)
                {
                    var x = -10200 + ix * 600;
                    var z = -10200 + iz * 600;
                    var h = TerrainGenerator.LandHeight(x, z, out _, out var ocean, out var desert, out _);
                    Assert.True(float.IsFinite(h) && h <= WorldGenerationConfig.DynamicReliefCeilingHeight,
                        $"unbounded Dynamic land at ({x},{z}): {h}");
                    if (ocean != 0f || desert != 0f) continue;
                    if (h > maximum) { maximum = h; highest = (seed, x, z); }
                    if (h < 50f) low++;
                    if (h > SkyConfig.CloudHeight)
                    {
                        high++;
                        if (previous.TryGetValue((ix - 1, iz), out var left) && left > SkyConfig.CloudHeight
                            || previous.TryGetValue((ix, iz - 1), out var near) && near > SkyConfig.CloudHeight)
                            pairs++;
                    }
                    previous[(ix, iz)] = h;
                }
            }
            output.WriteLine($"35x35 x 3 seeds, 600-block grid: {high} above clouds, {pairs} adjacent crest pairs, {low} lowland; highest {highest}={maximum}");
            Assert.True(high > 0 && pairs > 0, "no connected above-cloud crest in the bounded sample");
            Assert.True(low > 0, "no open lowland in the bounded sample");
            WorldSeed.Apply(highest.Seed, WorldGenProfile.Dynamic);
            for (var dz = -2; dz <= 2; dz++)
            {
                var row = new List<string>();
                for (var dx = -2; dx <= 2; dx++)
                {
                    var sx = highest.X + dx * 300;
                    var sz = highest.Z + dz * 300;
                    var sh = TerrainGenerator.LandHeight(sx, sz, out var sb, out _, out _, out _);
                    row.Add($"({sx},{sz}) {sh:F0}/{sb}");
                }
                output.WriteLine(string.Join(" | ", row));
            }
            var column = ColumnCache.Get(highest.X >> Chunk.SizeShift, highest.Z >> Chunk.SizeShift);
            var local = ((highest.X & Chunk.SizeMask) << Chunk.SizeShift) | (highest.Z & Chunk.SizeMask);
            Assert.InRange(column.Heights[local], int.MinValue, (int)WorldGenerationConfig.DynamicReliefCeilingHeight);
            if (BiomeTraits.ShowsBareRock(column.Biomes[local]))
                Assert.Equal(0, column.DirtDepth[local]);
            Assert.Equal(1f, SnowGenerator.ColdWeightAt(highest.X, highest.Z, column.Heights[local]));
        }
        finally { WorldSeed.Apply(WorldSeed.Default); }
    }

    [Fact]
    public void ValleyTransferIsContinuousNonraisingAndMonotone()
    {
        TestContent.EnsureLoaded();
        try
        {
            WorldSeed.Apply(WorldSeed.Default, WorldGenProfile.Dynamic);
            (int X, int Z)? compressed = null;
            for (var ix = 0; ix < 31 && compressed == null; ix++)
            for (var iz = 0; iz < 31; iz++)
            {
                var x = -9000 + ix * 600;
                var z = -9000 + iz * 600;
                if (TerrainRelief.FlattenValley(x, z, 50f) < 49f) { compressed = (x, z); break; }
            }
            Assert.NotNull(compressed);
            var (cx, cz) = compressed.Value;
            var previous = float.NegativeInfinity;
            for (var h = 0f; h <= 100f; h += 0.25f)
            {
                var transferred = TerrainRelief.FlattenValley(cx, cz, h);
                Assert.True(transferred <= h && transferred >= previous,
                    $"valley transfer raised or reversed at {h}: {transferred} after {previous}");
                previous = transferred;
            }
            var knee = WorldGenerationConfig.ValleyKnee;
            Assert.Equal(knee, TerrainRelief.FlattenValley(cx, cz, knee));
            Assert.InRange(knee - TerrainRelief.FlattenValley(cx, cz, knee - 0.01f), 0f, 0.02f);
            Assert.Equal(50f, TerrainRelief.FlattenValley(0, 0, 50f));
            output.WriteLine($"compressed valley at ({cx},{cz})");
        }
        finally { WorldSeed.Apply(WorldSeed.Default); }
    }

    [Fact]
    public void HashedKnollRisesOnAnAcceptedFlatValleyAndResetsDeterministically()
    {
        TestContent.EnsureLoaded();
        try
        {
            foreach (var seed in Seeds)
            {
                WorldSeed.Apply(seed, WorldGenProfile.Dynamic);
                for (var gx = -18; gx <= 18; gx++)
                for (var gz = -18; gz <= 18; gz++)
                {
                    var site = TerrainPeaks.KnollSiteAt(gx, gz);
                    if (!site.Exists) continue;
                    var x = site.CenterX;
                    var z = site.CenterZ;
                    var before = TerrainGenerator.PreFeatureLandHeight(x, z);
                    var after = TerrainGenerator.LandHeight(x, z, out _, out _, out _, out _);
                    Assert.True(after > before, $"accepted knoll did not lift its valley at ({x},{z})");
                    Assert.Equal(0f, TerrainPeaks.KnollHeight(x + site.Radius + 1, z));
                    WorldSeed.Apply(seed, WorldGenProfile.Dynamic);
                    Assert.Equal(site, TerrainPeaks.KnollSiteAt(gx, gz));
                    Assert.Equal(after, TerrainGenerator.LandHeight(x, z, out _, out _, out _, out _));
                    output.WriteLine($"knoll seed {seed} cell ({gx},{gz}) center ({x},{z}) {before}->{after}");
                    return;
                }
            }
            Assert.Fail("no accepted knoll on a flat valley in the bounded cells");
        }
        finally { WorldSeed.Apply(WorldSeed.Default); }
    }

    [Fact]
    public void DynamicSpawnClearingLeavesTheNewReliefAndSitesNeutral()
    {
        TestContent.EnsureLoaded();
        try
        {
            WorldSeed.Apply(WorldSeed.Default, WorldGenProfile.Dynamic);
            foreach (var (x, z) in new[] { (0f, 0f), (128f, -256f), (-320f, 50f) })
            {
                Assert.Equal(0f, TerrainRelief.CordilleraHeight(x, z));
                Assert.Equal(50f, TerrainRelief.FlattenValley(x, z, 50f));
                Assert.Equal(0f, TerrainPeaks.KnollHeight(x, z));
            }
        }
        finally { WorldSeed.Apply(WorldSeed.Default); }
    }

    [Fact]
    public void DynamicSurfaceWritersKeepTheirActualReachBelowTheCloudSlab()
    {
        TestContent.EnsureLoaded();
        try
        {
            WorldSeed.Apply(WorldSeed.Default, WorldGenProfile.Dynamic);
            var top = (int)WorldGenerationConfig.DynamicReliefCeilingHeight;
            Assert.True(SurfaceClearance.Allows(top, 1));
            Assert.False(SurfaceClearance.Allows(top, 1 + TreeGenerator.ReachUp));
            Assert.True(SurfaceClearance.Allows(top, WorldFeatureConfig.PillarMaxHeight));
            Assert.False(SurfaceClearance.Allows(top, TowerPlacement.MaxHeightAboveGround));
            Assert.True(top + 1 < WorldGenerationConfig.CloudSlabBottomY);
            WorldSeed.Apply(WorldSeed.Default, WorldGenProfile.Legacy);
            Assert.True(SurfaceClearance.Allows(top, 1 + TreeGenerator.ReachUp));
        }
        finally { WorldSeed.Apply(WorldSeed.Default); }
    }

    [Fact]
    public void DynamicScreeUsesTheGeneratedBareRockSurface()
    {
        TestContent.EnsureLoaded();
        try
        {
            foreach (var seed in Seeds)
            {
                WorldSeed.Apply(seed, WorldGenProfile.Dynamic);
                for (var ix = 0; ix < 31; ix++)
                for (var iz = 0; iz < 31; iz++)
                {
                    var x = -7500 + ix * 500;
                    var z = -7500 + iz * 500;
                    var h = TerrainGenerator.LandHeight(x, z, out var biome, out _, out _, out _);
                    if (!BiomeTraits.ShowsBareRock(biome) || !TerrainGenerator.IsScree(x, z, (int)h)) continue;
                    var cx = x >> Chunk.SizeShift;
                    var cz = z >> Chunk.SizeShift;
                    var column = ColumnCache.Get(cx, cz);
                    var local = ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);
                    var ground = column.Heights[local];
                    if (column.DirtDepth[local] != 0 || !TerrainGenerator.IsScree(x, z, ground)) continue;
                    var blocks = new ushort[Chunk.Size * Chunk.Size * Chunk.Size];
                    ColumnCache.GenerateColumn(blocks, cx, cz, x & Chunk.SizeMask, z & Chunk.SizeMask,
                        ground, column.Bed[local], column.DirtDepth[local], ground & ~Chunk.SizeMask,
                        column.Biomes[local]);
                    Assert.Equal(TerrainGenerator.GravelBlock,
                        blocks[Chunk.Idx(x & Chunk.SizeMask, ground & Chunk.SizeMask, z & Chunk.SizeMask)]);
                    output.WriteLine($"generated scree seed {seed} ({x},{z}) at Y {ground}");
                    return;
                }
            }
            Assert.Fail("no generated Dynamic scree surface in the bounded sample");
        }
        finally { WorldSeed.Apply(WorldSeed.Default); }
    }

    [Fact]
    public void DynamicSteepLodMeshesStayInsideTheRealTerrainAndIndexBudget()
    {
        TestContent.EnsureLoaded();
        try
        {
            WorldSeed.Apply(WorldSeed.Default, WorldGenProfile.Dynamic);
            // A fixed representative from the bounded range matrix; all levels cover its slope.
            const int x = 1200;
            const int z = 6600;
            foreach (var level in Enumerable.Range(0, LodRegion.Levels.Length))
            {
                var spec = LodRegion.Levels[level];
                var rx = (int)MathF.Floor(x / (float)spec.RegionBlocks);
                var rz = (int)MathF.Floor(z / (float)spec.RegionBlocks);
                var mesh = LodRegion.BuildMeshData(level, rx, rz);
                Assert.NotEmpty(mesh.Vertices);
                Assert.True(mesh.Vertices.Count <= ushort.MaxValue - 7);
                var checkedTops = 0;
                var stride = Math.Max(1, mesh.Vertices.Count / 128);
                for (var q = 0; q < mesh.Vertices.Count; q += 4 * stride)
                {
                    var v = mesh.Vertices[q];
                    if ((v.Normal & 7) != 4 || v.PackedColor >> 24 == 253) continue;
                    var vx = rx * spec.RegionBlocks + (int)v.Position.X;
                    var vz = rz * spec.RegionBlocks + (int)v.Position.Z;
                    if (!DryGroundMin(vx, vz, out var ground)) continue;
                    Assert.True(v.Position.Y <= ground, $"LOD {level} at ({vx},{vz}) rose above {ground}");
                    checkedTops++;
                }
                Assert.True(checkedTops > 0);
                output.WriteLine($"LOD {level} region ({rx},{rz}): vertices {mesh.Vertices.Count}, indices {mesh.Indices.Count}");
            }
        }
        finally { WorldSeed.Apply(WorldSeed.Default); }
    }

    private static bool DryGroundMin(int x, int z, out int ground)
    {
        ground = int.MaxValue;
        for (var fx = x - 1; fx <= x; fx++)
        for (var fz = z - 1; fz <= z; fz++)
        {
            var column = ColumnCache.Get(fx >> Chunk.SizeShift, fz >> Chunk.SizeShift);
            var local = ((fx & Chunk.SizeMask) << Chunk.SizeShift) | (fz & Chunk.SizeMask);
            var height = column.Heights[local];
            if (column.WaterLevel[local] > height) return false;
            ground = Math.Min(ground, height);
        }
        return true;
    }
}
