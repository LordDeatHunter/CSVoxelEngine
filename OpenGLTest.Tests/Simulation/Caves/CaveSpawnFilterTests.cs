using System.Collections.Generic;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Caves;
using VoxelEngine.WorldGen.Caves;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Caves;

/// <summary>
/// The cave spawn search's biome filter (<see cref="MobSpawner.TryFindCaveSpawn"/>): a floor is taken only when its
/// standing cell's cave biome is in the search's mask, the search reports that biome, and the biome is asked only
/// about the standing cell of a floor every other check already accepted.
/// </summary>
public sealed class CaveSpawnFilterTests
{
    public CaveSpawnFilterTests() => TestContent.EnsureLoaded();

    private static CaveSpawnSearch SearchFor(int mask) => new(
        MobConfig.SlimeSpawnMinRadiusBlocks, MobConfig.SlimeSpawnMaxRadiusBlocks,
        MobConfig.SlimeCaveSpawnTries, MobConfig.SlimeCaveSearchHalfHeight,
        MobConfig.SlimeCaveScanDepth, MobConfig.SlimeCaveMaxSkyLight,
        MobConfig.MobSpawnBlockLightLimit, mask);

    private static CaveBiomeResolver Always(CaveBiome biome) => (_, _, _, _) => biome;

    [Fact]
    public void AMaskWithoutTheResolvedBiomeRefusesEveryTry()
    {
        var chunks = CaveTestWorld.Cave(sun: 0, block: 0);
        var search = SearchFor(CaveSpawnRules.AllBiomes & ~CaveSpawnRules.Bit(CaveBiome.Fungal));
        for (var attempt = 0; attempt < 30; attempt++)
        {
            Assert.False(MobSpawner.TryFindCaveSpawn(chunks, CaveTestWorld.EyeOnFloor(), search,
                Always(CaveBiome.Fungal), out _, out _, out _, out var biome));
            Assert.Equal(CaveBiome.Plain, biome);
        }
    }

    [Fact]
    public void TheSearchReportsTheResolvedBiomeAndFindsTheFloorWhenAllowed()
    {
        var chunks = CaveTestWorld.Cave(sun: 0, block: 0);
        var search = SearchFor(CaveSpawnRules.Bit(CaveBiome.Fungal));
        var found = 0;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (!MobSpawner.TryFindCaveSpawn(chunks, CaveTestWorld.EyeOnFloor(), search,
                    Always(CaveBiome.Fungal), out _, out var groundY, out _, out var biome)) continue;
            found++;
            Assert.Equal(CaveTestWorld.Floor, groundY);
            Assert.Equal(CaveBiome.Fungal, biome);
        }
        Assert.True(found > 0, "an allowed cave biome never took a spawn");
    }

    [Fact]
    public void TheResolverIsAskedOnlyAboutTheStandingCellOfAnOtherwiseAcceptedFloor()
    {
        var asked = new List<int>();
        CaveBiomeResolver recording = (_, _, y, _) =>
        {
            asked.Add(y);
            return CaveBiome.Crystal;
        };
        var search = SearchFor(CaveSpawnRules.AllBiomes);

        var dark = CaveTestWorld.Cave(sun: 0, block: 0);
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (!MobSpawner.TryFindCaveSpawn(dark, CaveTestWorld.EyeOnFloor(), search, recording,
                    out _, out var groundY, out _, out _)) continue;
            Assert.All(asked, y => Assert.Equal(groundY + 1, y));
        }
        Assert.NotEmpty(asked);

        asked.Clear();
        var sunlit = CaveTestWorld.Cave(sun: 15, block: 0);
        for (var attempt = 0; attempt < 30; attempt++)
            Assert.False(MobSpawner.TryFindCaveSpawn(sunlit, CaveTestWorld.EyeOnFloor(), search, recording,
                out _, out _, out _, out _));
        Assert.Empty(asked);
    }
}
