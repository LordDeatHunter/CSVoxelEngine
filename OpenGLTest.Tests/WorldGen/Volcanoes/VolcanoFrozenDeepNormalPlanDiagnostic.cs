using System;
using System.Numerics;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features.Underground;
using VoxelEngine.WorldGen.Volcanoes;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen.Volcanoes;

/// <summary>One frozen deep normal fill and its lake-free probe counterpart.</summary>
[Collection("Worldgen seed")]
public sealed class VolcanoFrozenDeepNormalPlanDiagnostic(ITestOutputHelper output)
{
    private const string OptInVariable = "VOXEL_S5B_FROZEN_NORMAL_DEEP";
    private const int Cx = -942;
    private const int Cy = -14;
    private const int Cz = -488;

    [OptInNormalDeepFact]
    public void OneNormalAndOneProbePreserveOwnedDeepCells()
    {
        TestContent.EnsureLoaded();
        var previousSeed = WorldSeed.Value;
        var previousProfile = WorldSeed.Profile;
        var normalEntered = 0;
        var normalCompleted = 0;
        var probeEntered = 0;
        var probeCompleted = 0;
        var checkedCells = 0;
        var owned = 0;
        var frontier = 0;
        long planMisses = -1;
        try
        {
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            var site = VolcanoSites.SiteAt(-8, -4);
            Assert.True(site.Exists && site.Deep.Exists);
            Assert.Equal(-15057, site.Deep.AxisX);
            Assert.Equal(-7805, site.Deep.AxisZ);
            Assert.Equal(151, site.Floor);
            var column = ColumnCache.Get(Cx, Cz);
            var beforePlan = CaveLavaLakes.PlanCacheMisses;

            var pos = new Vector3(Cx, Cy, Cz);
            var normal = new Chunk(null!, pos, generateBlockData: false);
            normalEntered++;
            ChunkTerrainFill.GenerateBlockData(normal, stampGates: true, generateLakes: true);
            normalCompleted++;
            Assert.True(normal.IsBlockDataReady);
            planMisses = CaveLavaLakes.PlanCacheMisses - beforePlan;

            var probe = new Chunk(null!, pos, generateBlockData: false);
            probeEntered++;
            ChunkTerrainFill.GenerateBlockData(probe, stampGates: true, generateLakes: false);
            probeCompleted++;
            Assert.True(probe.IsBlockDataReady);
            Assert.Equal(planMisses, CaveLavaLakes.PlanCacheMisses - beforePlan);

            var lava = ContentRegistry.RequireBlock("lava").Id;
            var lining = ContentRegistry.RequireBlock("charred_stone").Id;
            for (var lx = 0; lx < Chunk.Size; lx++)
            for (var ly = 0; ly < Chunk.Size; ly++)
            for (var lz = 0; lz < Chunk.Size; lz++)
            {
                checkedCells++;
                var wx = Cx * Chunk.Size + lx;
                var wy = Cy * Chunk.Size + ly;
                var wz = Cz * Chunk.Size + lz;
                var height = column.Heights[(lx << Chunk.SizeShift) | lz];
                var kind = VolcanoShape.Classify(site, wx, wy, wz, height);
                if (kind == VolcanoCell.None) continue;
                owned++;
                var expected = kind == VolcanoCell.Lava ? lava
                    : kind == VolcanoCell.Air ? BlockType.Air : lining;
                Assert.Equal(expected, normal.GetLocalBlock(lx, ly, lz));
                Assert.Equal(expected, probe.GetLocalBlock(lx, ly, lz));
                var i = Chunk.Idx(lx, ly, lz);
                Assert.Equal((byte)0, normal.StateArray is null ? (byte)0 : normal.StateArray[i]);
                Assert.Equal((byte)0, probe.StateArray is null ? (byte)0 : probe.StateArray[i]);
            }

            foreach (var i in normal.FluidFrontier ?? [])
            {
                frontier++;
                var lx = i >> 8;
                var ly = (i >> 4) & Chunk.SizeMask;
                var lz = i & Chunk.SizeMask;
                var height = column.Heights[(lx << Chunk.SizeShift) | lz];
                Assert.Equal(VolcanoCell.None, VolcanoShape.Classify(site,
                    Cx * Chunk.Size + lx, Cy * Chunk.Size + ly, Cz * Chunk.Size + lz, height));
            }

            var ax = site.Deep.AxisX;
            var az = site.Deep.AxisZ;
            AssertAnchor(normal, probe, ax, -214, az, lava);
            AssertAnchor(normal, probe, ax - 1, -214, az, lava);
            AssertAnchor(normal, probe, ax - 2, -214, az - 2, lava);
            AssertAnchor(normal, probe, ax - 5, -209, az, lining);
            AssertAnchor(normal, probe, ax - 13, -213, az, BlockType.Air);
            AssertAnchor(normal, probe, ax - 13, -214, az, lava);
            Assert.Equal(Chunk.BlockCount, checkedCells);
            Assert.True(owned > 0);
            // A cache miss records a real Plan invocation, not its hashed admission or flood result.
            Assert.Equal(1, planMisses);
        }
        finally
        {
            try
            {
                output.WriteLine($"S5B_NORMAL_DEEP normal={normalCompleted}/{normalEntered} " +
                    $"probe={probeCompleted}/{probeEntered} checked={checkedCells}/{Chunk.BlockCount} " +
                    $"owned={owned} frontier={frontier} planCacheMisses={planMisses}; " +
                    "actual Plan admission/flood result remains unknown from this counter");
            }
            finally
            {
                WorldSeed.Apply(previousSeed, previousProfile);
            }
        }
    }

    private static void AssertAnchor(Chunk normal, Chunk probe, int x, int y, int z, ushort id)
    {
        Assert.Equal(Cx, x >> Chunk.SizeShift);
        Assert.Equal(Cy, y >> Chunk.SizeShift);
        Assert.Equal(Cz, z >> Chunk.SizeShift);
        var lx = x & Chunk.SizeMask;
        var ly = y & Chunk.SizeMask;
        var lz = z & Chunk.SizeMask;
        Assert.Equal(id, normal.GetLocalBlock(lx, ly, lz));
        Assert.Equal(id, probe.GetLocalBlock(lx, ly, lz));
    }

    /// <summary>Default discovery skips the expensive fixed normal Plan realization.</summary>
    public sealed class OptInNormalDeepFactAttribute : FactAttribute
    {
        public OptInNormalDeepFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
                Skip = $"Set {OptInVariable}=1 for the frozen deep normal Plan diagnostic.";
        }
    }
}
