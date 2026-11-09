using System;
using VoxelEngine.Entities.Goblins;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Caves;
using VoxelEngine.WorldGen.Caves;
using Xunit;

namespace VoxelEngine.Tests.Entities.Goblins;

/// <summary>
/// The goblin's cave spawn (<see cref="GoblinManager.TryFindCaveSpawn"/>, <see cref="GoblinManager.TryFindCavePackMate"/>):
/// a cave floor is kept by its cave biome's goblin weight, so goblins favor the crystal and fungal caves and never take
/// the magma deep, the body stands on the found floor, and a pack mate joins only its leader's cave biome.
/// </summary>
public sealed class GoblinCaveSpawnTests
{
    public GoblinCaveSpawnTests() => TestContent.EnsureLoaded();

    // The goblin ring is wide and the hand-built cave small, so a single call often finds nothing: loop
    private const int Calls = 300;

    private static CaveBiomeResolver Always(CaveBiome biome) => (_, _, _, _) => biome;

    [Fact]
    public void AGoblinNeverSpawnsInTheMagmaDeep()
    {
        var chunks = CaveTestWorld.Cave(sun: 0, block: 0);
        for (var call = 0; call < Calls; call++)
            Assert.False(GoblinManager.TryFindCaveSpawn(chunks, CaveTestWorld.EyeOnFloor(), roll: 0f,
                Always(CaveBiome.MagmaDeep), out _, out _));
    }

    [Fact]
    public void AFavouredCaveTakesARollAPlainCaveRefuses()
    {
        var roll = (CaveSpawnRules.GoblinWeight(CaveBiome.Fungal) + CaveSpawnRules.GoblinWeight(CaveBiome.Plain)) * 0.5f;
        var chunks = CaveTestWorld.Cave(sun: 0, block: 0);

        var fungal = 0;
        for (var call = 0; call < Calls; call++)
        {
            if (!GoblinManager.TryFindCaveSpawn(chunks, CaveTestWorld.EyeOnFloor(), roll, Always(CaveBiome.Fungal),
                    out _, out var biome)) continue;
            fungal++;
            Assert.Equal(CaveBiome.Fungal, biome);
        }
        Assert.True(fungal > 0, "a favored cave never took the roll");

        for (var call = 0; call < Calls; call++)
            Assert.False(GoblinManager.TryFindCaveSpawn(chunks, CaveTestWorld.EyeOnFloor(), roll,
                Always(CaveBiome.Plain), out _, out _));
    }

    [Fact]
    public void TheBodyStandsOnTheFoundFloor()
    {
        var chunks = CaveTestWorld.Cave(sun: 0, block: 0);
        var found = 0;
        for (var call = 0; call < Calls; call++)
        {
            if (!GoblinManager.TryFindCaveSpawn(chunks, CaveTestWorld.EyeOnFloor(), roll: 0f,
                    Always(CaveBiome.Crystal), out var position, out _)) continue;
            found++;
            Assert.Equal(CaveTestWorld.Floor + 1, position.Y - Goblin.Height * 0.5f, 3);
            Assert.Equal(0.5f, position.X - MathF.Floor(position.X), 3);
            Assert.Equal(0.5f, position.Z - MathF.Floor(position.Z), 3);
        }
        Assert.True(found > 0, "the cave never took a goblin");
    }

    [Fact]
    public void APackMateLandsOnAFloorNearTheLeaderInTheLeadersBiome()
    {
        var chunks = CaveTestWorld.Cave(sun: 0, block: 0);
        const int leaderX = 2;
        const int leaderZ = -3;
        // The leader's side of x is fungal, the far side crystal: no mate may land over there
        CaveBiomeResolver split = (_, x, _, _) => x <= leaderX ? CaveBiome.Fungal : CaveBiome.Crystal;

        var found = 0;
        for (var call = 0; call < Calls; call++)
        {
            if (!GoblinManager.TryFindCavePackMate(chunks, leaderX, CaveTestWorld.Floor, leaderZ, CaveBiome.Fungal,
                    split, out var mate)) continue;
            found++;
            var x = (int)MathF.Floor(mate.X);
            var z = (int)MathF.Floor(mate.Z);
            Assert.True(x <= leaderX, $"a mate landed at x {x}, outside the leader's biome");
            Assert.Equal(CaveTestWorld.Floor + 1, mate.Y - Goblin.Height * 0.5f, 3);
            var dx = x + 0.5f - (leaderX + 0.5f);
            var dz = z + 0.5f - (leaderZ + 0.5f);
            Assert.True(MathF.Sqrt(dx * dx + dz * dz) <= MobConfig.GoblinPackSpawnRadiusBlocks + 1.5f,
                "a mate landed beyond the pack radius");
        }
        Assert.True(found > 0, "no pack mate ever joined its leader");
    }
}
