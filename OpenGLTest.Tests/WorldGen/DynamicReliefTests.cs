using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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
    public void AHighCordilleraCrestContinuesAcrossARealDryLandWalk()
    {
        TestContent.EnsureLoaded();
        try
        {
            WorldSeed.Apply(WorldSeed.Default, WorldGenProfile.Dynamic);
            var starts = new List<(float X, float Z, float Support, float Height)>();
            for (var x = 600; x <= 1800; x += 300)
            for (var z = 6000; z <= 7200; z += 300)
            {
                var support = TerrainRelief.CordilleraSupport(x, z);
                if (support < 0.9f) continue;
                var height = TerrainGenerator.LandHeight(x, z, out _, out var ocean, out var desert, out _);
                if (ocean == 0f && desert == 0f && height > SkyConfig.CloudHeight)
                    starts.Add((x, z, support, height));
            }
            Assert.NotEmpty(starts);

            var longest = 0;
            var furthest = 0f;
            foreach (var start in starts.OrderByDescending(s => s.Height).Take(2))
            for (var dir = 0; dir < 8; dir++)
            {
                if (!TryWalkCrest(start, dir * MathF.PI / 4f, out var path))
                {
                    longest = Math.Max(longest, path.Count - 1);
                    var end = path[^1];
                    furthest = Math.Max(furthest, MathF.Sqrt(MathF.Pow(end.X - start.X, 2) + MathF.Pow(end.Z - start.Z, 2)));
                    continue;
                }
                output.WriteLine($"ridge start ({start.X},{start.Z}), support {start.Support:F3}, height {start.Height:F1}; {path.Count - 1} steps of 32 blocks");
                output.WriteLine(string.Join("; ", path.Select(p => $"({p.X:F0},{p.Z:F0}) s{p.Support:F2} h{p.Height:F0}")));
                return;
            }
            Assert.Fail($"no sustained dry, above-lowland cordillera walk from the bounded high-crest neighborhood; longest {longest} steps, furthest {furthest:F0} blocks");
        }
        finally { WorldSeed.Apply(WorldSeed.Default); }
    }

    private static bool TryWalkCrest((float X, float Z, float Support, float Height) start, float heading,
        out List<(float X, float Z, float Support, float Height)> path)
    {
        path = [start];
        var x = start.X;
        var z = start.Z;
        for (var step = 0; step < 48; step++)
        {
            (float X, float Z, float Support, float Height, float Heading)? best = null;
            var bestScore = float.NegativeInfinity;
            for (var turn = -1; turn <= 1; turn++)
            {
                var candidateHeading = heading + turn * MathF.PI / 8f;
                var nextX = x + 32f * MathF.Cos(candidateHeading);
                var nextZ = z + 32f * MathF.Sin(candidateHeading);
                var support = TerrainRelief.CordilleraSupport(nextX, nextZ);
                if (support < 0.5f) continue;
                var height = TerrainGenerator.LandHeight(nextX, nextZ, out _, out var ocean, out var desert, out _);
                if (ocean != 0f || desert != 0f || height < start.Height * 0.6f) continue;
                var score = support - 0.04f * Math.Abs(turn);
                if (score <= bestScore) continue;
                best = (nextX, nextZ, support, height, candidateHeading);
                bestScore = score;
            }
            if (best == null) return false;
            var chosen = best.Value;
            x = chosen.X;
            z = chosen.Z;
            heading = chosen.Heading;
            path.Add((x, z, chosen.Support, chosen.Height));
        }
        var dx = x - start.X;
        var dz = z - start.Z;
        return MathF.Sqrt(dx * dx + dz * dz) >= 1500f;
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
                var floor = WorldGenerationConfig.ValleyFloor;
                var lowerThird = floor + (WorldGenerationConfig.ValleyKnee - floor) / 3f;
                var slope = (TerrainRelief.FlattenValley(x, z, lowerThird)
                    - TerrainRelief.FlattenValley(x, z, floor)) / (lowerThird - floor);
                if (slope < 0.8f) { compressed = (x, z); break; }
            }
            Assert.NotNull(compressed);
            var (cx, cz) = compressed.Value;
            var low = WorldGenerationConfig.ValleyFloor;
            var lowBandEnd = low + (WorldGenerationConfig.ValleyKnee - low) / 3f;
            var secant = (TerrainRelief.FlattenValley(cx, cz, lowBandEnd)
                - TerrainRelief.FlattenValley(cx, cz, low)) / (lowBandEnd - low);
            Assert.True(secant < 0.8f, $"the lower valley band retained slope {secant}");
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
            output.WriteLine($"compressed valley at ({cx},{cz}), lower-third secant {secant:F3}");
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
    public void GeneratedSurfaceWritersLeaveTheClearRowsBelowTheCloudSlab()
    {
        TestContent.EnsureLoaded();
        try
        {
            WorldSeed.Apply(WorldSeed.Default, WorldGenProfile.Dynamic);
            const int centerX = 1200;
            const int centerZ = 6600;
            var centerCx = centerX >> Chunk.SizeShift;
            var centerCz = centerZ >> Chunk.SizeShift;
            for (var cx = centerCx - 1; cx <= centerCx + 1; cx++)
            for (var cz = centerCz - 1; cz <= centerCz + 1; cz++)
            {
                var chunk = new Chunk(null!, new Vector3(cx, 12, cz), generateBlockData: false);
                ChunkTerrainFill.GenerateBlockData(chunk);
                for (var lx = 0; lx < Chunk.Size; lx++)
                for (var lz = 0; lz < Chunk.Size; lz++)
                for (var y = 196; y <= 197; y++)
                    Assert.Equal(BlockType.Air, chunk.Storage.Get(Chunk.Idx(lx, y - 192, lz)));
            }
            output.WriteLine("nine Dynamic chunks at Y192..207 keep rows 196 and 197 clear");
        }
        finally { WorldSeed.Apply(WorldSeed.Default); }
    }

    [Fact]
    public void GeneratedDynamicLodSnowCapMatchesTheRealRockySurface()
    {
        TestContent.EnsureLoaded();
        try
        {
            WorldSeed.Apply(WorldSeed.Default, WorldGenProfile.Dynamic);
            (int CellX, int CellZ, int Ground)? chosen = null;
            for (var cellX = 1792; cellX < 1824 && chosen == null; cellX += 2)
            for (var cellZ = 6592; cellZ < 6624; cellZ += 2)
            {
                var x = cellX + 1;
                var z = cellZ + 1;
                var column = ColumnCache.Get(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
                var li = ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);
                var h = column.Heights[li];
                if (!BiomeTraits.ShowsBareRock(column.Biomes[li])
                    || h < TerrainGenerator.MountainRockHeight || (h & Chunk.SizeMask) == Chunk.SizeMask
                    || TerrainGenerator.IsWaterColumn(x, z, h)
                    || !SnowGenerator.HasSnow(x, z, SnowGenerator.ColdWeightAt(x, z, h))) continue;
                chosen = (cellX, cellZ, h);
                break;
            }
            Assert.NotNull(chosen);
            var (chosenX, chosenZ, ground) = chosen.Value;
            var nearX = chosenX + 1;
            var nearZ = chosenZ + 1;

            var cy = (ground + 1) >> Chunk.SizeShift;
            var chunk = new Chunk(null!, new Vector3(nearX >> Chunk.SizeShift, cy, nearZ >> Chunk.SizeShift),
                generateBlockData: false);
            ChunkTerrainFill.GenerateBlockData(chunk);
            var near = chunk.Storage.Get(Chunk.Idx(nearX & Chunk.SizeMask, (ground + 1) & Chunk.SizeMask,
                nearZ & Chunk.SizeMask));
            Assert.Equal(TerrainGenerator.SnowBlock, near);

            var spec = LodRegion.Levels[0];
            var rx = chosenX / spec.RegionBlocks;
            var rz = chosenZ / spec.RegionBlocks;
            var mesh = LodRegion.BuildMeshData(0, rx, rz);
            var localX = chosenX - rx * spec.RegionBlocks;
            var localZ = chosenZ - rz * spec.RegionBlocks;
            var topTiles = new List<ushort>();
            for (var q = 0; q + 3 < mesh.Vertices.Count; q += 4)
            {
                var v = mesh.Vertices[q];
                if ((v.Normal & 7) != 4 || v.PackedColor >> 24 == 253) continue;
                var minX = MathF.Min(MathF.Min(mesh.Vertices[q].Position.X, mesh.Vertices[q + 1].Position.X),
                    MathF.Min(mesh.Vertices[q + 2].Position.X, mesh.Vertices[q + 3].Position.X));
                var minZ = MathF.Min(MathF.Min(mesh.Vertices[q].Position.Z, mesh.Vertices[q + 1].Position.Z),
                    MathF.Min(mesh.Vertices[q + 2].Position.Z, mesh.Vertices[q + 3].Position.Z));
                if (minX == localX && minZ == localZ) topTiles.Add(v.Layer);
            }
            Assert.NotEmpty(topTiles);
            var snowTile = BlockType.ById[TerrainGenerator.SnowBlock]!.FaceTiles[4];
            Assert.Contains(snowTile, topTiles);
            output.WriteLine($"rocky snow cell ({nearX},{nearZ}) Y{ground}, near block {near}, LOD tiles [{string.Join(',', topTiles)}], snow tile {snowTile}");
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
