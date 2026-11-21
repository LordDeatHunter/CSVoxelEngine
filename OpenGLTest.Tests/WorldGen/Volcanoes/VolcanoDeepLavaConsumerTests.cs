using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features.Underground;
using VoxelEngine.WorldGen.Volcanoes;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Volcanoes;

/// <summary>Real cave-lake consumer and cache lifecycle controls for the deep caldera.</summary>
[Collection("Worldgen seed")]
public sealed class VolcanoDeepLavaConsumerTests
{
    [Fact]
    public void CaveFloodRejectsTheFrozenPoolFluidBoundary()
    {
        TestContent.EnsureLoaded();
        var previousSeed = WorldSeed.Value;
        var previousProfile = WorldSeed.Profile;
        try
        {
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            var site = VolcanoSites.SiteAt(-8, -4);
            Assert.True(site.Exists && site.Deep.Exists);
            Assert.Equal(-15057, site.Deep.AxisX);
            Assert.Equal(-7805, site.Deep.AxisZ);
            var x = site.Deep.AxisX + 13;
            var z = site.Deep.AxisZ;
            const int y = -213;
            Assert.Equal(VolcanoCell.Air, VolcanoShape.Classify(site, x, y, z, site.Floor));
            Assert.Equal(VolcanoCell.Lava, VolcanoShape.Classify(site, x, y - 1, z, site.Floor));
            var lava = ContentRegistry.RequireBlock("lava").Id;
            var lining = ContentRegistry.RequireBlock("charred_stone").Id;
            var calls = 0;
            var fluidCalls = 0;
            ushort Terrain(int px, int py, int pz)
            {
                calls++;
                var kind = VolcanoShape.Classify(site, px, py, pz, site.Floor);
                if (kind == VolcanoCell.Lava) fluidCalls++;
                return kind switch
                {
                    VolcanoCell.Air => BlockType.Air,
                    VolcanoCell.Lava => lava,
                    _ => lining
                };
            }

            var cells = CaveLavaLakes.Flood(new CaveLavaLakes.CellPosition(x, y, z), y,
                x >> WorldGenerationConfig.CaveLavaLakeCellShift,
                y >> WorldGenerationConfig.CaveLavaLakeCellShift,
                z >> WorldGenerationConfig.CaveLavaLakeCellShift, 1000, Terrain);
            Assert.Empty(cells);
            Assert.Equal(1, fluidCalls);
            Assert.InRange(calls, 2, 8);
        }
        finally
        {
            WorldSeed.Apply(previousSeed, previousProfile);
        }
    }

    [Fact]
    public void PlanCacheClearsOnProfileApplyAndOnlyTheLastWorldEnd()
    {
        TestContent.EnsureLoaded();
        var previousSeed = WorldSeed.Value;
        var previousProfile = WorldSeed.Profile;
        var owners = 0;
        try
        {
            WorldSeed.Apply(1337, WorldGenProfile.Legacy);
            var before = CaveLavaLakes.PlanCacheMisses;
            Assert.Empty(CaveLavaLakes.Plan(-236, 1, -122));
            Assert.Equal(before + 1, CaveLavaLakes.PlanCacheMisses);
            Assert.Empty(CaveLavaLakes.Plan(-236, 1, -122));
            Assert.Equal(before + 1, CaveLavaLakes.PlanCacheMisses);

            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            Assert.Empty(CaveLavaLakes.Plan(-236, 1, -122));
            Assert.Equal(before + 2, CaveLavaLakes.PlanCacheMisses);
            Assert.Empty(CaveLavaLakes.Plan(-236, 1, -122));
            Assert.Equal(before + 2, CaveLavaLakes.PlanCacheMisses);

            WorldSeed.BeginWorld(); owners++;
            WorldSeed.BeginWorld(); owners++;
            WorldSeed.EndWorld(); owners--;
            Assert.True(WorldSeed.WorldIsLive);
            Assert.Empty(CaveLavaLakes.Plan(-236, 1, -122));
            Assert.Equal(before + 2, CaveLavaLakes.PlanCacheMisses);
            WorldSeed.EndWorld(); owners--;
            Assert.False(WorldSeed.WorldIsLive);
            Assert.Empty(CaveLavaLakes.Plan(-236, 1, -122));
            Assert.Equal(before + 3, CaveLavaLakes.PlanCacheMisses);
        }
        finally
        {
            while (owners > 0)
            {
                WorldSeed.EndWorld();
                owners--;
            }
            WorldSeed.Apply(previousSeed, previousProfile);
        }
    }
}
