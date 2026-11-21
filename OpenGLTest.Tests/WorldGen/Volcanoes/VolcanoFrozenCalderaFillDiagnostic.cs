using System;
using System.Numerics;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Volcanoes;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen.Volcanoes;

/// <summary>Opt-in production fill control at the first observed natural S5a caldera.</summary>
[Collection("Worldgen seed")]
public sealed class VolcanoFrozenCalderaFillDiagnostic(ITestOutputHelper output)
{
    private const string OptInVariable = "VOXEL_S5A_FROZEN_CALDERA_FILL";
    private const int Seed = 1337;
    private const int ChunkY = 9;
    private const int ChunkZ = -488;
    private static readonly int[] ChunkX = [-942, -941];

    [OptInFillFact]
    public void RealNormalAndProbeFillsOwnTheFrozenLavaAndSharedFace()
    {
        TestContent.EnsureLoaded();
        var previousSeed = WorldSeed.Value;
        var previousProfile = WorldSeed.Profile;
        var columnEntered = 0;
        var columnCompleted = 0;
        var siteEntered = 0;
        var siteCompleted = 0;
        var normalEntered = 0;
        var normalCompleted = 0;
        var probeEntered = 0;
        var probeCompleted = 0;
        var directEntered = 0;
        var directCompleted = 0;
        var snapshots = 0;
        var ownedCells = 0;
        var lavaCells = 0;
        var liningCells = 0;
        var airCells = 0;
        var sharedFaceCells = 0;
        var frontierCells = 0;
        var piercedBedCells = 0;
        var retainedBedCells = 0;

        try
        {
            WorldSeed.Apply(Seed, WorldGenProfile.Dynamic);
            siteEntered++;
            var site = VolcanoSites.SiteAt(-8, -4);
            siteCompleted++;
            Assert.True(site.Exists);
            Assert.Equal(-15056.497f, site.X);
            Assert.Equal(-7804.5435f, site.Z);
            Assert.Equal(358.11472f, site.Radius);
            Assert.Equal(34.471203f, site.CraterRadius);
            Assert.Equal(151, site.Floor);
            Assert.Equal(6, site.LakeDepth);
            Assert.Equal(156.2266f, site.Rise);
            Assert.True(site.Deep.Exists);

            var lavaRadius = site.CraterRadius - WorldGenerationConfig.VolcanoLiningThickness;
            foreach (var x in new[] { ChunkX[0] * Chunk.Size, (ChunkX[1] + 1) * Chunk.Size - 1 })
            foreach (var z in new[] { ChunkZ * Chunk.Size, (ChunkZ + 1) * Chunk.Size - 1 })
                Assert.True(VolcanoShape.Distance(site, x, z) < lavaRadius);

            var lava = ContentRegistry.RequireBlock("lava").Id;
            var lining = ContentRegistry.RequireBlock("charred_stone").Id;
            var normalBlocks = new ushort[2][];
            var normalState = new byte[2][];
            var probeBlocks = new ushort[2][];
            var probeState = new byte[2][];

            for (var n = 0; n < ChunkX.Length; n++)
            {
                var cx = ChunkX[n];
                columnEntered++;
                var column = ColumnCache.Get(cx, ChunkZ);
                columnCompleted++;
                for (var lx = 0; lx < Chunk.Size; lx++)
                for (var lz = 0; lz < Chunk.Size; lz++)
                {
                    var ci = (lx << Chunk.SizeShift) | lz;
                    Assert.Equal(site.Floor, column.Heights[ci]);
                    Assert.Equal(site.Floor, column.Bed[ci]);
                    Assert.Equal(TerrainGenerator.NoWater, column.WaterLevel[ci]);
                    Assert.Equal(Biome.Charred, column.Biomes[ci]);
                    Assert.Equal((byte)0, column.DirtDepth[ci]);
                }

                var pos = new Vector3(cx, ChunkY, ChunkZ);
                var normal = new Chunk(null!, pos, generateBlockData: false);
                normalEntered++;
                ChunkTerrainFill.GenerateBlockData(normal, stampGates: true, generateLakes: true);
                normalCompleted++;
                Assert.True(normal.IsBlockDataReady);
                var normalSnapshot = Snapshot(normal);
                normalBlocks[n] = normalSnapshot.Blocks;
                normalState[n] = normalSnapshot.State;
                snapshots++;

                var probe = new Chunk(null!, pos, generateBlockData: false);
                probeEntered++;
                ChunkTerrainFill.GenerateBlockData(probe, stampGates: true, generateLakes: false);
                probeCompleted++;
                Assert.True(probe.IsBlockDataReady);
                var probeSnapshot = Snapshot(probe);
                probeBlocks[n] = probeSnapshot.Blocks;
                probeState[n] = probeSnapshot.State;
                snapshots++;

                frontierCells += normalSnapshot.Frontier.Length + probeSnapshot.Frontier.Length;
                Assert.Empty(normalSnapshot.Frontier);
                Assert.Empty(probeSnapshot.Frontier);
                for (var lx = 0; lx < Chunk.Size; lx++)
                for (var lz = 0; lz < Chunk.Size; lz++)
                for (var ly = 0; ly < Chunk.Size; ly++)
                {
                    var i = Chunk.Idx(lx, ly, lz);
                    var dx = (long)cx * Chunk.Size + lx - site.Deep.AxisX;
                    var dz = (long)ChunkZ * Chunk.Size + lz - site.Deep.AxisZ;
                    var core = dx * dx + dz * dz <= (long)WorldGenerationConfig.VolcanoConduitRadius
                        * WorldGenerationConfig.VolcanoConduitRadius;
                    // S5a's entire off-axis bed/lake stays fixed. Only the new integer shaft
                    // pierces the two bed levels in this frozen chunk band.
                    var expected = ly <= 1 && !core ? lining : ly <= 7 ? lava : BlockType.Air;
                    Assert.Equal(expected, normalBlocks[n][i]);
                    Assert.Equal(expected, probeBlocks[n][i]);
                    Assert.Equal((byte)0, normalState[n][i]);
                    Assert.Equal((byte)0, probeState[n][i]);
                    Assert.Equal(normalBlocks[n][i], probeBlocks[n][i]);
                    Assert.Equal(normalState[n][i], probeState[n][i]);
                    ownedCells++;
                    if (ly <= 1)
                    {
                        if (core) piercedBedCells++;
                        else retainedBedCells++;
                    }
                    if (expected == lava) lavaCells++;
                    else if (expected == lining) liningCells++;
                    else airCells++;
                }

                // Distinct from the full pipeline: a direct stamp must clear nonzero owned state
                // and leave the unowned air/state sentinels above the floor untouched.
                var sentinelBlocks = new ushort[Chunk.BlockCount];
                var sentinelState = new byte[Chunk.BlockCount];
                Array.Fill(sentinelState, (byte)0x7f);
                directEntered++;
                VolcanoStamp.Stamp(sentinelBlocks, sentinelState, column, cx * Chunk.Size,
                    ChunkY * Chunk.Size, ChunkZ * Chunk.Size);
                directCompleted++;
                for (var lx = 0; lx < Chunk.Size; lx++)
                for (var lz = 0; lz < Chunk.Size; lz++)
                for (var ly = 0; ly < Chunk.Size; ly++)
                {
                    var i = Chunk.Idx(lx, ly, lz);
                    var dx = (long)cx * Chunk.Size + lx - site.Deep.AxisX;
                    var dz = (long)ChunkZ * Chunk.Size + lz - site.Deep.AxisZ;
                    var core = dx * dx + dz * dz <= (long)WorldGenerationConfig.VolcanoConduitRadius
                        * WorldGenerationConfig.VolcanoConduitRadius;
                    Assert.Equal(ly <= 1 && !core ? lining : ly <= 7 ? lava : BlockType.Air,
                        sentinelBlocks[i]);
                    Assert.Equal(ly <= 7 ? (byte)0 : (byte)0x7f, sentinelState[i]);
                }
            }

            for (var ly = 0; ly < Chunk.Size; ly++)
            for (var lz = 0; lz < Chunk.Size; lz++)
            {
                var left = Chunk.Idx(Chunk.Size - 1, ly, lz);
                var right = Chunk.Idx(0, ly, lz);
                Assert.Equal(normalBlocks[0][left], normalBlocks[1][right]);
                Assert.Equal(normalState[0][left], normalState[1][right]);
                Assert.Equal(probeBlocks[0][left], probeBlocks[1][right]);
                Assert.Equal(probeState[0][left], probeState[1][right]);
                sharedFaceCells++;
            }

            Assert.Equal(2, normalCompleted);
            Assert.Equal(2, probeCompleted);
            Assert.Equal(2, directCompleted);
            Assert.True(piercedBedCells > 0);
            Assert.True(retainedBedCells > 0);
        }
        finally
        {
            try
            {
                output.WriteLine($"S5A_FROZEN_FILL site={siteCompleted}/{siteEntered} " +
                    $"columns={columnCompleted}/{columnEntered} normal={normalCompleted}/{normalEntered} " +
                    $"probe={probeCompleted}/{probeEntered} directStamp={directCompleted}/{directEntered} " +
                    $"snapshots={snapshots}/4 checkedCells={ownedCells}/8192 " +
                    $"lava={lavaCells} lining={liningCells} air={airCells} " +
                    $"piercedBed={piercedBedCells} retainedBed={retainedBedCells} " +
                    $"sharedFaceCells={sharedFaceCells}/256 frontierCells={frontierCells}");
            }
            finally
            {
                WorldSeed.Apply(previousSeed, previousProfile);
            }
        }
    }

    private static (ushort[] Blocks, byte[] State, ushort[] Frontier) Snapshot(Chunk chunk)
    {
        var blocks = new ushort[Chunk.BlockCount];
        for (var lx = 0; lx < Chunk.Size; lx++)
        for (var ly = 0; ly < Chunk.Size; ly++)
        for (var lz = 0; lz < Chunk.Size; lz++)
            blocks[Chunk.Idx(lx, ly, lz)] = chunk.GetLocalBlock(lx, ly, lz);
        return (blocks, chunk.StateArray is null ? new byte[Chunk.BlockCount]
            : (byte[])chunk.StateArray.Clone(), chunk.FluidFrontier is null ? []
            : (ushort[])chunk.FluidFrontier.Clone());
    }

    /// <summary>Default discovery cannot execute the fixed full-fill diagnostic.</summary>
    public sealed class OptInFillFactAttribute : FactAttribute
    {
        public OptInFillFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
                Skip = $"Set {OptInVariable}=1 for the frozen real caldera fill.";
        }
    }
}
