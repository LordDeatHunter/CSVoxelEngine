using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Volcanoes;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen.Volcanoes;

/// <summary>Opt-in normal/probe fill control at the frozen natural S5a caldera.</summary>
[Collection("Worldgen seed")]
public sealed class VolcanoFrozenDeepPipelineDiagnostic(ITestOutputHelper output)
{
    private const string OptInVariable = "VOXEL_S5B_FROZEN_DEEP_PIPELINE";
    private const int ChunkZ = -488;
    private static readonly int[] ChunkX = [-942, -941];
    private static readonly int[] ChunkY = [8, 4, -14, -12];

    [OptInPipelineFact]
    public void RealNormalAndProbeChunksOwnTheFrozenConnectedDeepVolume()
    {
        TestContent.EnsureLoaded();
        var previousSeed = WorldSeed.Value;
        var previousProfile = WorldSeed.Profile;
        var siteEntered = 0;
        var siteCompleted = 0;
        var boxEntered = 0;
        var boxCompleted = 0;
        var columnEntered = 0;
        var columnCompleted = 0;
        var normalEntered = 0;
        var normalCompleted = 0;
        var probeEntered = 0;
        var probeCompleted = 0;
        var snapshots = new Dictionary<(int X, int Y, bool Normal), Snapshot>();
        var columns = new Dictionary<int, ColumnData>();
        var checkedCells = 0;
        var checkedHeights = 0;
        var ownedCells = 0;
        var comparedCells = 0;
        var frontierCells = 0;
        var anchors = 0;

        try
        {
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            siteEntered++;
            var site = VolcanoSites.SiteAt(-8, -4);
            siteCompleted++;
            Assert.True(site.Exists && site.Deep.Exists);
            Assert.Equal(-15057, site.Deep.AxisX);
            Assert.Equal(-7805, site.Deep.AxisZ);
            Assert.Equal(151, site.Floor);

            var outsideOldGuardX = site.Deep.AxisX + 45;
            Assert.True(VolcanoShape.Distance(site, outsideOldGuardX, site.Deep.AxisZ)
                > site.CraterRadius + WorldGenerationConfig.VolcanoGuard);
            boxEntered++;
            Assert.True(VolcanoSites.IntersectsOwnedBox(outsideOldGuardX, -208,
                site.Deep.AxisZ, outsideOldGuardX, -208, site.Deep.AxisZ));
            boxCompleted++;
            boxEntered++;
            Assert.False(VolcanoSites.IntersectsOwnedBox(outsideOldGuardX, 0,
                site.Deep.AxisZ, outsideOldGuardX, 0, site.Deep.AxisZ));
            boxCompleted++;

            foreach (var cx in ChunkX)
            {
                columnEntered++;
                var column = ColumnCache.Get(cx, ChunkZ);
                columns.Add(cx, column);
                columnCompleted++;
                foreach (var height in column.Heights)
                {
                    Assert.True(height > VolcanoShape.ChamberTop);
                    checkedHeights++;
                }
            }

            foreach (var cy in ChunkY)
            foreach (var cx in ChunkX)
            {
                var pos = new Vector3(cx, cy, ChunkZ);
                if (cy >= 4)
                {
                    var normal = new Chunk(null!, pos, generateBlockData: false);
                    normalEntered++;
                    ChunkTerrainFill.GenerateBlockData(normal, stampGates: true, generateLakes: true);
                    normalCompleted++;
                    Assert.True(normal.IsBlockDataReady);
                    snapshots.Add((cx, cy, true), Capture(normal));
                }

                var probe = new Chunk(null!, pos, generateBlockData: false);
                probeEntered++;
                ChunkTerrainFill.GenerateBlockData(probe, stampGates: true, generateLakes: false);
                probeCompleted++;
                Assert.True(probe.IsBlockDataReady);
                snapshots.Add((cx, cy, false), Capture(probe));
            }

            var lava = ContentRegistry.RequireBlock("lava").Id;
            var lining = ContentRegistry.RequireBlock("charred_stone").Id;
            foreach (var (key, snapshot) in snapshots)
            {
                var (cx, cy, normal) = key;
                var column = columns[cx];
                for (var lx = 0; lx < Chunk.Size; lx++)
                for (var lz = 0; lz < Chunk.Size; lz++)
                for (var ly = 0; ly < Chunk.Size; ly++)
                {
                    checkedCells++;
                    var wx = cx * Chunk.Size + lx;
                    var wy = cy * Chunk.Size + ly;
                    var wz = ChunkZ * Chunk.Size + lz;
                    var i = Chunk.Idx(lx, ly, lz);
                    var height = column.Heights[(lx << Chunk.SizeShift) | lz];
                    var kind = VolcanoShape.Classify(site, wx, wy, wz, height);
                    if (kind == VolcanoCell.None) continue;
                    ownedCells++;
                    Assert.Equal(kind == VolcanoCell.Lava ? lava : kind == VolcanoCell.Air
                        ? BlockType.Air : lining, snapshot.Blocks[i]);
                    Assert.Equal((byte)0, snapshot.State[i]);
                    if (!normal || !snapshots.TryGetValue((cx, cy, false), out var other)) continue;
                    Assert.Equal(snapshot.Blocks[i], other.Blocks[i]);
                    Assert.Equal(snapshot.State[i], other.State[i]);
                    comparedCells++;
                }

                foreach (var i in snapshot.Frontier)
                {
                    frontierCells++;
                    var lx = i >> 8;
                    var ly = (i >> 4) & Chunk.SizeMask;
                    var lz = i & Chunk.SizeMask;
                    var height = column.Heights[(lx << Chunk.SizeShift) | lz];
                    Assert.Equal(VolcanoCell.None, VolcanoShape.Classify(site,
                        cx * Chunk.Size + lx, cy * Chunk.Size + ly,
                        ChunkZ * Chunk.Size + lz, height));
                }
            }

            var ax = site.Deep.AxisX;
            var az = site.Deep.AxisZ;
            // These semantic anchors do not derive their expected ids from Classify.
            foreach (var normal in new[] { true, false })
            {
                AssertCell(snapshots, ax, 143, az, normal, lava); anchors++;
                AssertCell(snapshots, ax + 1, 143, az, normal, lava); anchors++;
                AssertCell(snapshots, ax + 5, 143, az, normal, lining); anchors++;
                AssertCell(snapshots, ax, 64, az, normal, lava); anchors++;
                AssertCell(snapshots, ax + 1, 64, az, normal, lava); anchors++;
                AssertCell(snapshots, ax + 5, 64, az, normal, lining); anchors++;
            }
            AssertCell(snapshots, ax, -214, az, false, lava); anchors++;
            AssertCell(snapshots, ax + 1, -214, az, false, lava); anchors++;
            AssertCell(snapshots, ax + 2, -214, az + 2, false, lava); anchors++;
            AssertCell(snapshots, ax + 13, -185, az, false, BlockType.Air); anchors++;
            AssertCell(snapshots, ax + 13, -184, az, false, lining); anchors++;
            Assert.Equal(4, normalCompleted);
            Assert.Equal(8, probeCompleted);
            Assert.Equal(12, snapshots.Count);
            Assert.True(ownedCells > 0);
            Assert.True(comparedCells > 0);
        }
        finally
        {
            try
            {
                output.WriteLine($"S5B_FROZEN_DEEP site={siteCompleted}/{siteEntered} " +
                    $"ownedBox={boxCompleted}/{boxEntered} columns={columnCompleted}/{columnEntered} " +
                    $"normal={normalCompleted}/{normalEntered} probe={probeCompleted}/{probeEntered} " +
                    $"snapshots={snapshots.Count}/12 heights={checkedHeights}/512 " +
                    $"checked={checkedCells}/49152 " +
                    $"owned={ownedCells} compared={comparedCells} anchors={anchors}/17 " +
                    $"frontierVisited={frontierCells}");
            }
            finally
            {
                WorldSeed.Apply(previousSeed, previousProfile);
            }
        }
    }

    private static void AssertCell(Dictionary<(int X, int Y, bool Normal), Snapshot> snapshots,
        int x, int y, int z, bool normal, ushort expected)
    {
        var cx = x >> Chunk.SizeShift;
        var cy = y >> Chunk.SizeShift;
        var cz = z >> Chunk.SizeShift;
        Assert.Equal(ChunkZ, cz);
        var snapshot = snapshots[(cx, cy, normal)];
        var i = Chunk.Idx(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask);
        Assert.Equal(expected, snapshot.Blocks[i]);
        Assert.Equal((byte)0, snapshot.State[i]);
    }

    private static Snapshot Capture(Chunk chunk)
    {
        var blocks = new ushort[Chunk.BlockCount];
        for (var lx = 0; lx < Chunk.Size; lx++)
        for (var ly = 0; ly < Chunk.Size; ly++)
        for (var lz = 0; lz < Chunk.Size; lz++)
            blocks[Chunk.Idx(lx, ly, lz)] = chunk.GetLocalBlock(lx, ly, lz);
        return new Snapshot(blocks, chunk.StateArray is null ? new byte[Chunk.BlockCount]
            : (byte[])chunk.StateArray.Clone(), chunk.FluidFrontier is null ? []
            : (ushort[])chunk.FluidFrontier.Clone());
    }

    private sealed record Snapshot(ushort[] Blocks, byte[] State, ushort[] Frontier);

    /// <summary>Default discovery cannot execute the fixed full-pipeline diagnostic.</summary>
    public sealed class OptInPipelineFactAttribute : FactAttribute
    {
        public OptInPipelineFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
                Skip = $"Set {OptInVariable}=1 for the frozen deep normal/probe fills.";
        }
    }
}
