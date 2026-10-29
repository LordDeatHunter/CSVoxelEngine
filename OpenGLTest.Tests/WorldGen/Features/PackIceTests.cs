using System;
using System.Collections.Generic;
using VoxelEngine.Lod;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using VoxelEngine.WorldGen.Features.Underground;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Features;

/// <summary>
/// Covers the pack-ice edge of cold water (<see cref="SnowGenerator.IsFrozenWater"/>): ice only
/// forms past the band's start and is solid past the freeze line, the share rises with the cold,
/// hot springs never freeze, the near stamp follows the rule, and the edge is coherent floes and
/// leads rather than a contour line or per-column noise.
/// </summary>
public sealed class PackIceTests
{
    public PackIceTests() => TestContent.EnsureLoaded();

    private const float Start = WorldGenerationConfig.PackIceColdStart;
    private const float Freeze = WorldGenerationConfig.FreezeColdThreshold;
    private const int TopFace = 4;

    [Fact]
    public void IceThickensMonotonicallyWithCold()
    {
        // The near stamp lays snow only above the snow line and the far LOD covers a frozen sheet
        // in snow only above it, so the band must start at or above it, and must exist at all.
        Assert.True(Start >= WorldGenerationConfig.SnowColdThreshold);
        Assert.True(Start < Freeze);

        var flippedInBand = 0;
        for (var x = -1024; x <= 1024; x += 37)
        for (var z = -1024; z <= 1024; z += 37)
        {
            if (HotSpringGenerator.IsSite(x, z)) continue;
            var previous = false;
            for (var step = 0; step <= 100; step++)
            {
                var cold = step / 100f;
                var frozen = SnowGenerator.IsFrozenWater(x, z, cold);
                if (cold <= Start) Assert.False(frozen, $"ice at ({x}, {z}) below the band, cold {cold}");
                if (cold > Freeze) Assert.True(frozen, $"open water at ({x}, {z}) past the freeze line, cold {cold}");
                Assert.False(previous && !frozen, $"ice at ({x}, {z}) thawed as the cold rose to {cold}");
                if (frozen && !previous && cold <= Freeze) flippedInBand++;
                previous = frozen;
            }
        }

        Assert.True(flippedInBand > 0, "no column froze inside the pack-ice band");
    }

    [Fact]
    public void HotSpringsNeverFreeze()
    {
        var sites = 0;
        for (var x = -2048; x <= 2048 && sites < 3; x += 4)
        for (var z = -2048; z <= 2048 && sites < 3; z += 4)
        {
            if (!HotSpringGenerator.IsSite(x, z)) continue;
            Assert.False(SnowGenerator.IsFrozenWater(x, z, 1f), $"the hot spring at ({x}, {z}) froze");
            sites++;
        }

        Assert.True(sites > 0, "no hot spring site found in the scanned region");
    }

    [Fact]
    public void TheNearSeaCellAgreesWithTheRule()
    {
        const int perKind = 20;
        var frozenChecked = 0;
        var openChecked = 0;
        // Cold seas in the band are rarer than solid ones, so the window is wide
        for (var cx = -256; cx <= 256 && (frozenChecked < perKind || openChecked < perKind); cx++)
        for (var cz = -256; cz <= 256 && (frozenChecked < perKind || openChecked < perKind); cz++)
        {
            var x = cx * Chunk.Size + 8;
            var z = cz * Chunk.Size + 8;
            if (TerrainGenerator.ColdWeight(x, z) <= Start - 0.05f) continue;
            if (!SnowIceTests.IsSeaColumn(x, z)) continue;

            var height = ColumnCache.Get(cx, cz).Heights[VoxelCoords.ColumnIndex(8, 8)];
            var frozen = SnowGenerator.IsFrozenWater(x, z, SnowGenerator.ColdWeightAt(x, z, height));
            if (frozen ? frozenChecked >= perKind : openChecked >= perKind) continue;

            // A feature standing in the sea (a stack) owns its cell; only water or ice tells
            var cell = SnowIceTests.SurfaceSeaCell(x, z);
            if (cell != TerrainGenerator.IceBlock && cell != TerrainGenerator.SeaBlock) continue;

            Assert.True(frozen == (cell == TerrainGenerator.IceBlock),
                $"the sea at ({x}, {z}) is {(frozen ? "open" : "frozen")} against the pack-ice rule");
            if (frozen) frozenChecked++;
            else openChecked++;
        }

        Assert.True(frozenChecked > 0, "no frozen sea column was checked");
        Assert.True(openChecked > 0, "no open cold sea column was checked");
    }

    [Fact]
    public void TheFarLodSheetAgreesWithTheNearIce()
    {
        const int maxRegions = 12;
        var spec = LodRegion.Levels[0];
        var snowTiles = new HashSet<ushort>(BlockType.ById[TerrainGenerator.SnowBlock]!.FaceTiles);
        var regions = new HashSet<(int Rx, int Rz)>();
        var frozenCompared = 0;
        var openCompared = 0;
        var regionWithBoth = false;

        // Level-0 regions over band ocean; the band is sparse near the origin, so the scan is wide
        for (var cx = -256; cx <= 256 && regions.Count < maxRegions && !regionWithBoth; cx++)
        for (var cz = -256; cz <= 256 && regions.Count < maxRegions && !regionWithBoth; cz++)
        {
            var x = cx * Chunk.Size + 8;
            var z = cz * Chunk.Size + 8;
            var cold = TerrainGenerator.ColdWeight(x, z);
            if (cold <= Start || cold > Freeze) continue;
            if (TerrainGenerator.BiomeAt(x, z) != Biome.Ocean) continue;

            var rx = (int)Math.Floor(x / (float)spec.RegionBlocks);
            var rz = (int)Math.Floor(z / (float)spec.RegionBlocks);
            if (!regions.Add((rx, rz))) continue;

            var mesh = LodRegion.BuildMeshData(0, rx, rz);
            var frozenHere = 0;
            var openHere = 0;
            for (var q = 0; q + 3 < mesh.WaterVertices.Count; q += 4)
            {
                var first = mesh.WaterVertices[q];
                if ((first.Normal & 7) != TopFace) continue;

                var minX = float.MaxValue;
                var minZ = float.MaxValue;
                for (var c = 0; c < 4; c++)
                {
                    minX = MathF.Min(minX, mesh.WaterVertices[q + c].Position.X);
                    minZ = MathF.Min(minZ, mesh.WaterVertices[q + c].Position.Z);
                }

                // The sheet vertices are region-local: every cell's corner lies inside the region
                Assert.InRange(minX, 0f, spec.RegionBlocks - spec.CellSize);
                Assert.InRange(minZ, 0f, spec.RegionBlocks - spec.CellSize);
                var i = (int)minX / spec.CellSize;
                var j = (int)minZ / spec.CellSize;
                var centerX = rx * spec.RegionBlocks + i * spec.CellSize + spec.CellSize / 2;
                var centerZ = rz * spec.RegionBlocks + j * spec.CellSize + spec.CellSize / 2;

                // Below the rock line the sheet's cold is the plain climate cold, the near stamp's too
                if (TerrainGenerator.WaterLevelAt(centerX, centerZ) > WorldGenerationConfig.MountainRockHeight)
                    continue;

                var open = (first.PackedColor >> 24) == 254u;
                var frozen = SnowGenerator.IsFrozenWater(centerX, centerZ,
                    TerrainGenerator.ColdWeight(centerX, centerZ));
                Assert.True(frozen != open,
                    $"the far sheet at ({centerX}, {centerZ}) is {(open ? "open" : "frozen")} against the pack-ice rule");
                if (open)
                {
                    openHere++;
                    continue;
                }

                // A frozen cell in or past the band always lies above the snow line, as on the near ice
                Assert.Contains(first.Layer, snowTiles);
                frozenHere++;
            }

            frozenCompared += frozenHere;
            openCompared += openHere;
            regionWithBoth |= frozenHere > 0 && openHere > 0;
        }

        Assert.True(regionWithBoth, "no far-LOD region over the pack-ice band holds both ice and open water");
        Assert.True(frozenCompared > 0 && openCompared > 0, "no frozen or no open far sheet cell was compared");
    }

    [Fact]
    public void TheIceEdgeIsRaggedPatchesNotAContour()
    {
        const int stride = 2;
        const int halfWindow = 192;
        const int cells = 2 * halfWindow / stride + 1;
        var width = Freeze - Start;
        var mid = (Start + Freeze) / 2f;

        // Windows centered on columns near the middle of the band, far enough apart not to overlap
        var centers = new List<(int X, int Z)>();
        for (var x = -2048; x <= 2048 && centers.Count < 3; x += 32)
        for (var z = -2048; z <= 2048 && centers.Count < 3; z += 32)
        {
            if (MathF.Abs(TerrainGenerator.ColdWeight(x, z) - mid) > width / 8f) continue;
            var apart = true;
            foreach (var (cx, cz) in centers)
                apart &= Math.Max(Math.Abs(cx - x), Math.Abs(cz - z)) > 2 * halfWindow;
            if (apart) centers.Add((x, z));
        }
        Assert.NotEmpty(centers);

        var buckets = new Dictionary<int, (bool Frozen, bool Open)>();
        var thirdCount = new int[3];
        var thirdFrozen = new int[3];
        var bandColumns = 0;
        var frozenColumns = 0;
        var pairs = 0;
        var agreeing = 0;
        foreach (var (centerX, centerZ) in centers)
        {
            // 0 outside the band (or a spring), 1 open, 2 frozen
            var grid = new byte[cells, cells];
            for (var i = 0; i < cells; i++)
            for (var j = 0; j < cells; j++)
            {
                var x = centerX - halfWindow + i * stride;
                var z = centerZ - halfWindow + j * stride;
                var cold = TerrainGenerator.ColdWeight(x, z);
                if (cold <= Start || cold > Freeze) continue;
                if (HotSpringGenerator.IsSite(x, z)) continue;

                var frozen = SnowGenerator.IsFrozenWater(x, z, cold);
                grid[i, j] = frozen ? (byte)2 : (byte)1;
                bandColumns++;
                if (frozen) frozenColumns++;

                var third = Math.Min(2, (int)((cold - Start) / width * 3f));
                thirdCount[third]++;
                if (frozen) thirdFrozen[third]++;
                if (third != 1) continue;
                var key = (int)MathF.Floor(cold * 100f);
                buckets.TryGetValue(key, out var seen);
                buckets[key] = (seen.Frozen || frozen, seen.Open || !frozen);
            }

            for (var i = 0; i < cells; i++)
            for (var j = 0; j < cells; j++)
            {
                if (grid[i, j] == 0) continue;
                if (i + 1 < cells && grid[i + 1, j] != 0)
                {
                    pairs++;
                    if (grid[i + 1, j] == grid[i, j]) agreeing++;
                }
                if (j + 1 < cells && grid[i, j + 1] != 0)
                {
                    pairs++;
                    if (grid[i, j + 1] == grid[i, j]) agreeing++;
                }
            }
        }

        Assert.True(bandColumns > 0 && pairs > 0, "no pack-ice band columns in the scanned windows");
        foreach (var count in thirdCount)
            Assert.True(count > 0, "a third of the pack-ice band was never sampled");

        // (a) A contour would freeze every column of one cold alike; the floes mix them.
        var mixedBucket = false;
        foreach (var (frozen, open) in buckets.Values)
            mixedBucket |= frozen && open;
        Assert.True(mixedBucket, "no cold bucket in the middle of the band holds both ice and open water");

        // (b) Coherent floes: neighbors agree far more often than independent coins would.
        var p = frozenColumns / (float)bandColumns;
        var coinRate = p * p + (1f - p) * (1f - p);
        var agreement = agreeing / (float)pairs;
        Assert.True(agreement > coinRate + 0.1f,
            $"neighbors agree {agreement:F3} of the time against {coinRate:F3} for independent columns");

        // (c) The ice share rises from the warm end of the band to the cold end.
        var lowShare = thirdFrozen[0] / (float)thirdCount[0];
        var highShare = thirdFrozen[2] / (float)thirdCount[2];
        Assert.True(highShare > lowShare,
            $"the ice share falls across the band: {lowShare:F3} warm, {highShare:F3} cold");
    }
}
