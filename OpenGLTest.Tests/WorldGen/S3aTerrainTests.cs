using System;
using System.Collections.Generic;
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
    public void ThreeRealCoreSurfaceBandsStayBareThroughFullNeighborAwareFill()
    {
        TestContent.EnsureLoaded();
        var direct = 0;
        var voxels = 0;
        var fills = 0;
        try
        {
            TerrainErg.ReadCountsEnabled = true;
            foreach (var (seed, cx, cz) in new[]
            {
                (1337, -512, -448), (42, -432, -432), (-4242, -417, -32)
            })
            {
                WorldSeed.Apply(seed, WorldGenProfile.Dynamic);
                direct++;
                Assert.True(TerrainErg.IsCore(cx, cz));
                if (seed == -4242)
                {
                    // This core column meets the real outside-root tree's reach at its
                    // negative-X edge, so neighbor-rooted writers are exercised.
                    Assert.True(-6666 + 9 >= (cx << Chunk.SizeShift)
                        && -6666 - 9 <= (cx << Chunk.SizeShift) + Chunk.SizeMask
                        && -513 + 9 >= (cz << Chunk.SizeShift)
                        && -513 - 9 <= (cz << Chunk.SizeShift) + Chunk.SizeMask);
                    direct++;
                    Assert.False(TreeGenerator.TryGetTree(-6666, -513, out _, out _));
                }

                var column = ColumnCache.Get(cx, cz);
                direct++;
                var firstY = (column.MinHeight - 10) >> Chunk.SizeShift;
                var lastY = (column.MaxHeight + 32) >> Chunk.SizeShift;
                // Exact reservation from the earlier source-owned planner: 4 each.
                Assert.Equal(4, lastY - firstY + 1);
                Assert.True(fills + lastY - firstY + 1 <= 12);
                output.WriteLine($"reserve seed={seed} min={column.MinHeight} max={column.MaxHeight} vertical={firstY}..{lastY} calls={lastY - firstY + 1}");
                var chunks = new Dictionary<int, Chunk>();
                for (var cy = firstY; cy <= lastY; cy++)
                {
                    var chunk = new Chunk(null!, new Vector3(cx, cy, cz), generateBlockData: false);
                    fills++;
                    ChunkTerrainFill.GenerateBlockData(chunk);
                    chunks.Add(cy, chunk);
                }
                var caveDecorations = 0;
                for (var x = 0; x < Chunk.Size; x++)
                for (var z = 0; z < Chunk.Size; z++)
                {
                    var height = column.Heights[(x << Chunk.SizeShift) | z];
                    for (var y = height - 10; y <= height; y++)
                    {
                        voxels++;
                        var block = chunks[y >> Chunk.SizeShift].Storage.Get(Chunk.Idx(x, y & Chunk.SizeMask, z));
                        if (block == TerrainGenerator.SandBlock || block == BlockType.Air) continue;
                        // The full normal pipeline retains underground cave decorations in carved air.
                        // Seed 42's real stalactite at y=36 is below its local surface, not scenery.
                        var name = BlockType.ById[block]?.Name;
                        var caveDecoration = y < height && (name is "stalactite" or "stalagmite" or "glow_mushroom"
                            || name?.EndsWith("_stalactite", StringComparison.Ordinal) == true
                            || name?.EndsWith("_stalagmite", StringComparison.Ordinal) == true);
                        if (caveDecoration) { caveDecorations++; continue; }
                        Assert.Fail($"Core upper cover seed={seed} ({cx * Chunk.Size + x},{y},{cz * Chunk.Size + z}) id={block} name={name}");
                    }
                    for (var y = height + 1; y <= column.MaxHeight + 32; y++)
                    {
                        voxels++;
                        var block = chunks[y >> Chunk.SizeShift].Storage.Get(Chunk.Idx(x, y & Chunk.SizeMask, z));
                        Assert.Equal(BlockType.Air, block);
                    }
                }
                output.WriteLine($"bare seed={seed} inspected_voxels={voxels} underground_decorations={caveDecorations} normal_fills={fills}");
            }
        }
        finally
        {
            var (builds, biomes) = TerrainErg.CoreReadCounts;
            output.WriteLine($"S3a fullband direct={direct} voxel_reads={voxels} normal_fills={fills} cumulative_builds={builds} cumulative_biomes={biomes}");
            WorldSeed.Apply(WorldSeed.Default);
            TerrainErg.ReadCountsEnabled = false;
        }
    }

    [Fact]
    public void OutsideHardCoreErgThinsWithoutChangingLegacyOrZeroSupport()
    {
        TestContent.EnsureLoaded();
        var direct = 0;
        try
        {
            TerrainErg.ReadCountsEnabled = true;
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            const int startX = -8184;
            const int z = 2312;
            var found = false;
            for (var step = 1; step <= 64; step++)
            {
                var x = startX + step * Chunk.Size;
                direct++;
                if (TerrainErg.IsCore(x >> Chunk.SizeShift, z >> Chunk.SizeShift)) continue;
                direct++;
                var retention = TerrainErg.Retention(x, z);
                if (retention <= 0f || retention >= 1f) continue;
                found = true;
                output.WriteLine($"erg edge x={x} z={z} retained={retention:F3}");
                WorldSeed.Apply(1337, WorldGenProfile.Legacy);
                direct++;
                Assert.Equal(1f, TerrainErg.Retention(x, z));
                break;
            }
            Assert.True(found, "Expected a non-core Dynamic desert fringe with partial retention in the bounded eastward section");
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            direct++;
            Assert.Equal(1f, TerrainErg.Retention(0, 0)); // spawn forest has no desert support
        }
        finally
        {
            var (builds, biomes) = TerrainErg.CoreReadCounts;
            output.WriteLine($"S3a soft edge direct={direct} normal_fills=0 cumulative_builds={builds} cumulative_biomes={biomes}");
            WorldSeed.Apply(WorldSeed.Default);
            TerrainErg.ReadCountsEnabled = false;
        }
    }

    [Fact]
    public void CoreVerdictAndDuneHeightRepeatAfterSeedAndProfileReset()
    {
        TestContent.EnsureLoaded();
        var direct = 0;
        try
        {
            TerrainErg.ReadCountsEnabled = true;
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
            Assert.False(TerrainErg.IsCore(x >> Chunk.SizeShift, z >> Chunk.SizeShift));
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
            TerrainErg.ReadCountsEnabled = false;
        }
    }

    [Fact]
    public void GeneratedFarCoreKeepsSandOnExposedSides()
    {
        TestContent.EnsureLoaded();
        try
        {
            TerrainErg.ReadCountsEnabled = true;
            WorldSeed.Apply(42, WorldGenProfile.Dynamic);
            const int x = -6904;
            const int z = -6904;
            var spec = LodRegion.Levels[0];
            var rx = (int)MathF.Floor((float)x / spec.RegionBlocks);
            var rz = (int)MathF.Floor((float)z / spec.RegionBlocks);
            var mesh = LodRegion.BuildMeshData(0, rx, rz);
            var sand = VoxelEngine.BlockType.ById[ContentRegistry.RequireBlock("sand").Id]!.FaceTiles[0];
            var sandstone = VoxelEngine.BlockType.ById[ContentRegistry.RequireBlock("sandstone").Id]!.FaceTiles[0];
            var upperSideSand = 0;
            var upperSideSandstone = 0;
            var depthHistogram = new int[8, 2];
            for (var i = 0; i < mesh.Vertices.Count; i += 4)
            {
                var vertex = mesh.Vertices[i];
                if ((vertex.Normal & 7) >= 4) continue; // four lateral faces, never the cap
                var localX = (mesh.Vertices[i].Position.X + mesh.Vertices[i + 2].Position.X) * 0.5f;
                var localZ = (mesh.Vertices[i].Position.Z + mesh.Vertices[i + 2].Position.Z) * 0.5f;
                if (localX < 1 || localX > spec.RegionBlocks - 1 || localZ < 1 || localZ > spec.RegionBlocks - 1)
                    continue;
                var worldX = rx * spec.RegionBlocks + (int)localX;
                var worldZ = rz * spec.RegionBlocks + (int)localZ;
                if (!TerrainErg.IsCore(worldX >> Chunk.SizeShift, worldZ >> Chunk.SizeShift)) continue;
                var coreColumn = ColumnCache.Get(worldX >> Chunk.SizeShift, worldZ >> Chunk.SizeShift);
                var ground = coreColumn.Heights[((worldX & Chunk.SizeMask) << Chunk.SizeShift) | (worldZ & Chunk.SizeMask)];
                var faceY = (mesh.Vertices[i].Position.Y + mesh.Vertices[i + 2].Position.Y) * 0.5f;
                if (faceY >= ground || faceY < ground - WorldGenerationConfig.SandstoneDepth - 6)
                    continue;
                var depthBin = Math.Clamp((int)(ground - faceY), 0, 7);
                if (vertex.Layer == sand) { upperSideSand++; depthHistogram[depthBin, 0]++; }
                if (vertex.Layer == sandstone) { upperSideSandstone++; depthHistogram[depthBin, 1]++; }
            }
            output.WriteLine($"generated core LOD below-top sand={upperSideSand} sandstone={upperSideSandstone}; direct cells={spec.Cells * spec.Cells}; normal_fills=0");
            for (var bin = 0; bin < 8; bin++) output.WriteLine($"LOD depth {bin}: sand={depthHistogram[bin, 0]} sandstone={depthHistogram[bin, 1]}");
            Assert.True(upperSideSand > 0);
            Assert.Equal(0, upperSideSandstone);
        }
        finally
        {
            var (builds, biomes) = TerrainErg.CoreReadCounts;
            output.WriteLine($"S3a internal core_builds={builds} stored_biomes={biomes}");
            WorldSeed.Apply(WorldSeed.Default);
            TerrainErg.ReadCountsEnabled = false;
        }
    }

    [Fact]
    public void WindCrossSectionHasGentleStossAndSteeperSlipFace()
    {
        TestContent.EnsureLoaded();
        var direct = 0;
        try
        {
            TerrainErg.ReadCountsEnabled = true;
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
            var maxActualRise = 0f;
            var maxActualFall = 0f;
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
                maxActualRise = Math.Max(maxActualRise, height - priorHeight);
                maxActualFall = Math.Max(maxActualFall, priorHeight - height);
                minHeight = Math.Min(minHeight, height);
                maxHeight = Math.Max(maxHeight, height);
                priorDune = dune;
                priorHeight = height;
            }
            output.WriteLine($"wind section rise={maxRise:F3} fall={maxFall:F3} actual_rise={maxActualRise:F3} actual_fall={maxActualFall:F3} actual_height_span={maxHeight - minHeight:F3}");
            Assert.True(maxFall > maxRise * 1.5f);
            Assert.True(maxHeight - minHeight > 5f);
            Assert.True(maxActualFall > maxActualRise * 1.5f);
        }
        finally
        {
            output.WriteLine($"S3a wind direct={direct} normal_fills=0");
            var (builds, biomes) = TerrainErg.CoreReadCounts;
            output.WriteLine($"S3a internal core_builds={builds} stored_biomes={biomes}");
            WorldSeed.Apply(WorldSeed.Default);
            TerrainErg.ReadCountsEnabled = false;
        }
    }

    [Fact]
    public void WholeTreeAndOasisSitesOutsideCoreCannotIntrude()
    {
        TestContent.EnsureLoaded();
        var direct = 0;
        try
        {
            TerrainErg.ReadCountsEnabled = true;
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
            TerrainErg.ReadCountsEnabled = false;
        }
    }

    [Fact]
    public void ThreeSeedsContainSubstantialConnectedHardCores()
    {
        TestContent.EnsureLoaded();
        var direct = 0;
        try
        {
            TerrainErg.ReadCountsEnabled = true;
            foreach (var (seed, cx, cz) in new[]
            {
                (1337, -512, -448), (42, -432, -432), (-4242, -416, -32)
            })
            {
                WorldSeed.Apply(seed, WorldGenProfile.Dynamic);
                var seen = new System.Collections.Generic.HashSet<(int X, int Z)> { (cx, cz) };
                var pending = new System.Collections.Generic.Queue<(int X, int Z)>();
                pending.Enqueue((cx, cz));
                var connected = 0;
                while (pending.Count > 0 && connected < 24)
                {
                    var site = pending.Dequeue();
                    direct++;
                    if (!TerrainErg.IsCore(site.X, site.Z)) continue;
                    connected++;
                    foreach (var neighbor in new[]
                    {
                        (site.X + 1, site.Z), (site.X - 1, site.Z),
                        (site.X, site.Z + 1), (site.X, site.Z - 1)
                    })
                    {
                        if (seen.Count >= 64 || !seen.Add(neighbor)) continue;
                        pending.Enqueue(neighbor);
                    }
                }
                var (builds, biomes) = TerrainErg.CoreReadCounts;
                output.WriteLine($"connected seed={seed} core={connected} distinct={seen.Count} queried={direct} cumulative_builds={builds} cumulative_biomes={biomes}");
                Assert.True(seen.Count <= 64 && connected >= 24,
                    $"Bounded core component too small for seed {seed}: {connected} accepted, {seen.Count} visited");
            }
        }
        finally
        {
            output.WriteLine($"S3a connected direct={direct} normal_fills=0");
            var (builds, biomes) = TerrainErg.CoreReadCounts;
            output.WriteLine($"S3a internal core_builds={builds} stored_biomes={biomes}");
            WorldSeed.Apply(WorldSeed.Default);
            TerrainErg.ReadCountsEnabled = false;
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
            TerrainErg.ReadCountsEnabled = true;
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
            TerrainErg.ReadCountsEnabled = false;
        }
    }

}
