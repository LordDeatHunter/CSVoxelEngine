using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.StatusEffects;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Wildlife;
using VoxelEngine.Networking;
using VoxelEngine.Simulation;
using VoxelEngine.Status;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Entities.Wildlife;

/// <summary>
/// The sea's animals, driven headless in a flooded pool: a swimmer never leaves the water of its own accord, a beached
/// one falls, flops and suffocates, a fish darts away from a threat and calms down, a shark bites only a swimmer in
/// its reach and a jellyfish stings only what touches it, and the spawn rules keep the deep-sea kinds out of shallow
/// and fresh water.
/// </summary>
public sealed class SeaLifeTests
{
    public SeaLifeTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;
    private const int PoolDepth = 10;

    private static readonly WildlifeKind[] SeaKinds =
        Enum.GetValues<WildlifeKind>().Where(WildlifeSpecies.Swims).ToArray();

    private static StubTarget PlayerAt(float x, float y, float z) =>
        new(new Vector3(x, y, z)) { Effects = new StatusEffectSet() };

    private static WildlifeManager NewManager() =>
        new(WildlifeSpecies.Names.Select(EntityRegistry.Require));

    private static Animal Spawned(WildlifeManager manager, WildlifeKind kind, Vector3 center) =>
        manager.Spawn(WildlifeSpecies.NameOf(kind), center, spawnIn: false)!;

    private static bool RunUntil(WildlifeManager manager, ChunkManager chunks, IReadOnlyList<IMobTarget> targets,
        float seconds, Func<bool> done)
    {
        var drops = new DropManager();
        for (var t = 0f; t < seconds; t += Dt)
        {
            manager.Update(Dt, chunks, drops, targets);
            if (done()) return true;
        }
        return false;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b) =>
        MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    [Fact]
    public void ASwimmerWandersTheWaterAndNeverLeavesIt()
    {
        var chunks = MobTestWorld.Pool(PoolDepth);
        var manager = NewManager();
        var swimmers = SeaKinds.Select(kind => (AquaticAnimal)Spawned(manager, kind, new Vector3(8f, 5f, 8f))).ToArray();
        // Somebody far away keeps them in play (a mob with no one to wander for stands still)
        var bystander = PlayerAt(40f, 5f, 40f);

        var surface = PoolDepth + 1f;
        var left = false;
        RunUntil(manager, chunks, [bystander], 40f, () =>
        {
            foreach (var swimmer in swimmers)
            {
                var cell = swimmer.Position.BlockCell();
                var wet = BlockType.IsFluid(chunks.GetBlockGlobal(cell.X, cell.Y, cell.Z));
                if (!wet || swimmer.IsBeached || swimmer.Position.Y + swimmer.BodyHalfHeight > surface + 0.05f) left = true;
            }
            return left;
        });

        Assert.False(left, "a swimmer left the water or broke the surface");
        Assert.All(swimmers, swimmer => Assert.True(swimmer.Swims));
    }

    [Fact]
    public void ABeachedFishFallsFlopsAndSuffocatesUntilItIsBackInWater()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var manager = NewManager();
        var fish = (Fish)Spawned(manager, WildlifeKind.Tuna, new Vector3(8f, 4f, 8f));
        var bystander = PlayerAt(40f, 2f, 40f);
        var start = fish.Health;

        Assert.False(fish.IsBeached);
        RunUntil(manager, chunks, [bystander], 1f, () => fish.IsGrounded);

        Assert.True(fish.IsBeached);
        Assert.True(fish.IsGrounded, "a beached fish falls to the ground");
        Assert.True((fish.Posture & 2) != 0, "a client's mirror must be told it is beached");

        RunUntil(manager, chunks, [bystander], WildlifeConfig.BeachedDamageIntervalSeconds * 1.2f, () => false);
        Assert.True(fish.Health < start, "a fish out of water suffocates");
    }

    [Fact]
    public void AFishDartsAwayFromAThreatAndCalmsOnceItIsGone()
    {
        var chunks = MobTestWorld.Pool(PoolDepth);
        var manager = NewManager();
        var fish = (Fish)Spawned(manager, WildlifeKind.Cod, new Vector3(8f, 5f, 8f));
        var threat = PlayerAt(10f, 5.5f, 8f);
        var before = HorizontalDistance(fish.Position, threat.Eye);

        Assert.True(fish.Heeds([threat]));
        var darted = RunUntil(manager, chunks, [threat], 1.5f, () => (fish.Posture & 1) != 0);
        Assert.True(darted, "a fleeing fish plays its dart gait on every mirror");
        RunUntil(manager, chunks, [threat], 0.5f, () => false);
        Assert.True(HorizontalDistance(fish.Position, threat.Eye) > before, "a fish swims away from a threat");

        var gone = PlayerAt(60f, 5.5f, 8f);
        Assert.False(fish.Heeds([gone]));
        RunUntil(manager, chunks, [gone], 3f, () => (fish.Posture & 1) == 0);
        Assert.True((fish.Posture & 1) == 0, "it calms once nothing threatens it");
    }

    [Fact]
    public void ASharkBitesASwimmerInReachAndNobodyOutOfItsRange()
    {
        var chunks = MobTestWorld.Pool(PoolDepth);
        var manager = NewManager();
        var shark = (Shark)Spawned(manager, WildlifeKind.Shark, new Vector3(8f, 5f, 8f));

        var distant = PlayerAt(8f + WildlifeConfig.SharkNoticeRangeBlocks + 20f, 5.5f, 8f);
        Assert.False(shark.Heeds([distant]));
        RunUntil(manager, chunks, [distant], 6f, () => false);
        Assert.Equal(0, distant.DamageTaken);

        var swimmer = PlayerAt(8f + WildlifeConfig.SharkBiteReach * 0.5f, 5.5f, 8f);
        Assert.True(shark.Heeds([swimmer]));
        var bitten = RunUntil(manager, chunks, [swimmer], 6f, () => swimmer.DamageTaken > 0);
        Assert.True(bitten, "a shark bites a swimmer next to it");
        Assert.Equal(0, swimmer.DamageTaken % shark.StrikeDamage);
    }

    [Fact]
    public void AJellyfishStingsOnlyWhatTouchesItAndNeverChases()
    {
        var chunks = MobTestWorld.Pool(PoolDepth);
        var manager = NewManager();
        var jelly = (Jellyfish)Spawned(manager, WildlifeKind.Jellyfish, new Vector3(8f, 5f, 8f));

        var passerby = PlayerAt(8f + WildlifeConfig.JellyfishStingReach * 3f, 5.5f, 8f);
        var from = jelly.Position;
        RunUntil(manager, chunks, [passerby], 4f, () => false);
        Assert.Equal(0, passerby.DamageTaken);
        Assert.True(HorizontalDistance(jelly.Position, from) < WildlifeConfig.JellyfishDriftSpeed * 4f + 0.5f,
            "a jellyfish drifts, it does not give chase");

        var touching = PlayerAt(jelly.Position.X + WildlifeConfig.JellyfishStingReach * 0.4f, jelly.Position.Y + 0.4f,
            jelly.Position.Z);
        var stung = RunUntil(manager, chunks, [touching], 5f, () => touching.DamageTaken > 0);
        Assert.True(stung, "a jellyfish stings what touches it");
        Assert.True(touching.Effects!.IsActive(StatusEffectRegistry.Require(Jellyfish.VenomEffect)));
    }

    [Fact]
    public void TheDeepSeaKindsNeverSpawnInShallowOrFreshWater()
    {
        foreach (var rule in WildlifeSpawnRules.Sea)
        {
            Assert.True(WildlifeSpecies.Swims(rule.Kind));
            Assert.True(rule.LivesIn(Biome.Ocean, rule.MinDepth));
            Assert.False(rule.LivesIn(Biome.Ocean, rule.MinDepth - 1), $"{rule.Kind} needs its depth");
            Assert.Equal(!rule.OceanOnly, rule.LivesIn(Biome.Forest, rule.MinDepth));
        }

        for (var roll = 0f; roll < 1f; roll += 0.05f)
        {
            var lake = WildlifeSpawnRules.PickSea(Biome.Forest, 30, roll, _ => true);
            Assert.True(lake is { OceanOnly: false }, "only the kinds of any water live in a lake");
            Assert.Null(WildlifeSpawnRules.PickSea(Biome.Ocean, 0, roll, _ => true));
        }
    }

    [Fact]
    public void EverySeaKindHasItsOwnWireKindThatMapsBack()
    {
        foreach (var kind in SeaKinds)
        {
            var wire = NetEntity.KindOf(kind);
            Assert.True(NetEntity.TryGetWildlife(wire, out var back));
            Assert.Equal(kind, back);
            Assert.True(NetEntity.IsMob(wire));
            Assert.IsAssignableFrom<AquaticAnimal>(WildlifeSpecies.Create(EntityRegistry.Require(WildlifeSpecies.NameOf(kind)),
                Vector3.Zero));
        }
    }
}
