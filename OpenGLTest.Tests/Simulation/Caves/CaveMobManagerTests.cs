using System;
using System.Numerics;
using VoxelEngine.Content.StatusEffects;
using VoxelEngine.Entities;
using VoxelEngine.Entities.CaveMobs;
using VoxelEngine.Player;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Caves;
using VoxelEngine.Status;
using VoxelEngine.WorldGen.Caves;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Caves;

/// <summary>
/// The cave mobs' manager (<see cref="CaveMobManager"/>): its ring spawner lands only on dark cave floors around an
/// underground target, each sub-biome takes only its own kind (plain and echo caves none), a spawn takes the depth
/// tier of its floor and never passes the natural cap, a forced spawn passes the natural cap but not the hard cap and
/// keeps an explicit tier wherever it stands, and each kind's attack lands its own status effect.
/// </summary>
public sealed class CaveMobManagerTests
{
    public CaveMobManagerTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    // Deep enough under the echo bottom for a non-zero tier, still over the barrier band, all in one chunk layer
    private const int DeepFloor = -264;

    private static CaveMobManager NewManager(MobCaps? caps = null) => new(
        EntityRegistry.Require(Shardling.EntityName), EntityRegistry.Require(Sporeling.EntityName),
        EntityRegistry.Require(Magmaborn.EntityName), caps);

    private static CaveBiomeResolver Always(CaveBiome biome) => (_, _, _, _) => biome;

    // Runs the ring spawner long enough to try many spawns around a flying (so harmless) target standing on the
    // cave floor, the cave biome forced to the given one
    private static CaveMobManager SpawnRun(CaveBiome biome, int floor = CaveTestWorld.Floor, int updates = 600,
        MobCaps? caps = null)
    {
        var chunks = CaveTestWorld.Cave(sun: 0, block: 0, floor);
        var manager = NewManager(caps);
        manager.Biomes = Always(biome);
        var drops = new DropManager();
        IMobTarget[] targets = [new StubTarget(CaveTestWorld.EyeOnFloor(floor)) { IsFlying = true }];
        for (var i = 0; i < updates; i++)
        {
            manager.Update(0.5f, chunks, drops, targets);
            Assert.True(manager.LivingCount <= MobConfig.MobNaturalCap,
                $"{manager.LivingCount} cave mobs passed the natural cap");
        }
        return manager;
    }

    private static ChunkManager StoneFloor() => MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);

    // Body center of a mob of the given height standing on the y = 0 floor
    private static Vector3 OnFloor(float x, float z, float height) => new(x, 1f + height * 0.5f, z);

    private static StubTarget PlayerAt(float x, float z) =>
        new(StubTarget.EyeAt(new Vector3(x, 1f, z))) { Effects = new StatusEffectSet() };

    [Fact]
    public void TheRingSpawnerFillsItsCaveUpToTheNaturalCapAndNoFurther()
    {
        var manager = SpawnRun(CaveBiome.Crystal, updates: 800);
        Assert.True(manager.LivingCount > 0, "nothing spawned in a crystal cave");
        foreach (var mob in manager.Mobs)
        {
            Assert.IsType<Shardling>(mob);
            var feet = MathUtils.FloorToInt(mob.Feet.Y + 0.01f);
            Assert.InRange(feet, CaveTestWorld.Floor + 1, CaveTestWorld.Floor + 3);
        }
    }

    [Theory]
    [InlineData(CaveBiome.Crystal, typeof(Shardling))]
    [InlineData(CaveBiome.Fungal, typeof(Sporeling))]
    [InlineData(CaveBiome.MagmaDeep, typeof(Magmaborn))]
    public void EachSubBiomeSpawnsOnlyItsOwnKind(CaveBiome biome, Type kind)
    {
        var manager = SpawnRun(biome, updates: 200);
        Assert.NotEmpty(manager.Mobs);
        Assert.All(manager.Mobs, mob => Assert.IsType(kind, mob));
    }

    [Theory]
    [InlineData(CaveBiome.Plain)]
    [InlineData(CaveBiome.FrozenEcho)]
    [InlineData(CaveBiome.DesertEcho)]
    public void PlainAndEchoCavesSpawnNothing(CaveBiome biome) =>
        Assert.Empty(SpawnRun(biome, updates: 200).Mobs);

    [Fact]
    public void ATargetOnTheSurfaceSpawnsNothing()
    {
        var chunks = CaveTestWorld.Cave(sun: 0, block: 0);
        var manager = NewManager();
        manager.Biomes = Always(CaveBiome.Crystal);
        var surface = ColumnCache.GetHeights(0, 0)[VoxelCoords.ColumnIndex(0, 0)];
        IMobTarget[] targets = [new StubTarget(new Vector3(0.5f, surface + 1 + Camera.EyeHeight, 0.5f)) { IsFlying = true }];
        var drops = new DropManager();
        for (var i = 0; i < 200; i++)
            manager.Update(0.5f, chunks, drops, targets);
        Assert.Empty(manager.Mobs);
    }

    [Fact]
    public void ARingSpawnTakesTheTierOfItsFloor()
    {
        var expected = CaveDifficulty.TierAt(DeepFloor + 1);
        Assert.True(expected > 0, "the deep floor should lie in a tier past the first");

        var manager = SpawnRun(CaveBiome.MagmaDeep, DeepFloor, updates: 200);
        Assert.NotEmpty(manager.Mobs);
        Assert.All(manager.Mobs, mob => Assert.Equal(expected, mob.Tier));

        var shallow = SpawnRun(CaveBiome.MagmaDeep, updates: 200);
        Assert.NotEmpty(shallow.Mobs);
        Assert.All(shallow.Mobs, mob => Assert.Equal(CaveDifficulty.TierAt(CaveTestWorld.Floor + 1), mob.Tier));
    }

    [Fact]
    public void AForcedSpawnPassesTheNaturalCapButNotTheHardCap()
    {
        var caps = new MobCaps();
        var manager = NewManager(caps);
        var at = new Vector3(0.5f, CaveTestWorld.Floor + 2f, 0.5f);
        for (var i = 0; i < MobConfig.MobNaturalCap; i++)
            Assert.NotNull(manager.Spawn(Sporeling.EntityName, at));
        Assert.False(caps.AllowsNaturalSpawn);

        while (caps.ForcedRoom > 0)
            Assert.NotNull(manager.Spawn(Magmaborn.EntityName, at, tier: 1));
        Assert.Null(manager.Spawn(Shardling.EntityName, at));
        Assert.Null(manager.Spawn(Shardling.EntityName, at, tier: 2));
        Assert.Equal(MobConfig.MobHardCap, caps.Living);
    }

    [Fact]
    public void AnExplicitTierSpawnKeepsItsTierWhereverItStands()
    {
        var manager = NewManager();
        var shallow = manager.Spawn(Sporeling.EntityName, new Vector3(0.5f, 60f, 0.5f), tier: 3);
        var deep = manager.Spawn(Sporeling.EntityName, new Vector3(0.5f, DeepFloor + 2f, 0.5f), tier: 3);
        Assert.NotNull(shallow);
        Assert.NotNull(deep);
        Assert.Equal(3, shallow.Tier);
        Assert.Equal(shallow.Tier, deep.Tier);
        Assert.Equal(shallow.MaxHealth, deep.MaxHealth);

        // Without a tier the spawn reads it from where it stands
        var fromDepth = manager.Spawn(Sporeling.EntityName, new Vector3(0.5f, DeepFloor + 2f, 0.5f));
        Assert.NotNull(fromDepth);
        Assert.Equal(CaveDifficulty.TierAt(DeepFloor + 2f), fromDepth.Tier);
    }

    [Fact]
    public void AShardThatTouchesATargetHurtsItAndAppliesFrostburn()
    {
        var chunks = StoneFloor();
        var frostburn = StatusEffectRegistry.Require("frostburn");
        var manager = new CaveMobManager(EntityRegistry.Require(Shardling.EntityName), null, null);
        var drops = new DropManager();

        var target = PlayerAt(10.5f, 8.5f);
        var chest = RealmMob.ChestOf(target.Eye);
        manager.TestLaunchShard(chest - new Vector3(4f, 0f, 0f), new Vector3(20f, 3f, 0f), damage: 5);
        for (var i = 0; i < 60 && manager.Shards.Count > 0; i++)
            manager.Update(Dt, chunks, drops, [target]);

        Assert.Empty(manager.Shards);
        Assert.Equal(5, target.DamageTaken);
        Assert.True(target.Effects!.IsActive(frostburn));

        var flier = new StubTarget(target.Eye) { Effects = new StatusEffectSet(), IsFlying = true };
        manager.TestLaunchShard(chest - new Vector3(4f, 0f, 0f), new Vector3(20f, 3f, 0f), damage: 5);
        for (var i = 0; i < 60 && manager.Shards.Count > 0; i++)
            manager.Update(Dt, chunks, drops, [flier]);
        Assert.Equal(0, flier.DamageTaken);
        Assert.False(flier.Effects.IsActive(frostburn));
    }

    [Fact]
    public void ASporelingBurstAppliesSporesToEveryTargetInsideItOnly()
    {
        var chunks = StoneFloor();
        var spore = StatusEffectRegistry.Require("spore");
        var manager = new CaveMobManager(null, EntityRegistry.Require(Sporeling.EntityName), null);
        var drops = new DropManager();
        var sporeling = manager.Spawn(Sporeling.EntityName, OnFloor(6.5f, 8.5f, MobConfig.CaveSporelingHeight), 0,
            spawnIn: false);
        Assert.NotNull(sporeling);

        var near = PlayerAt(6.5f + MobConfig.CaveSporelingTriggerReach * 0.6f, 8.5f);
        var beside = PlayerAt(6.5f, 8.5f + MobConfig.CaveSporelingTriggerReach * 0.6f);
        var far = PlayerAt(6.5f, 8.5f + MobConfig.CaveSporeBurstRadius + 4f);
        IMobTarget[] targets = [near, beside, far];
        for (var i = 0; i < (int)(6f / Dt) && near.DamageTaken == 0; i++)
            manager.Update(Dt, chunks, drops, targets);

        Assert.True(near.DamageTaken > 0, "the burst never landed on a target in trigger reach");
        Assert.True(near.Effects!.IsActive(spore));
        Assert.True(beside.DamageTaken > 0, "the burst missed a second target inside it");
        Assert.True(beside.Effects!.IsActive(spore));
        Assert.Equal(0, far.DamageTaken);
        Assert.False(far.Effects!.IsActive(spore));
    }

    [Fact]
    public void AMagmabornBlowSetsItsTargetBurning()
    {
        var chunks = StoneFloor();
        var burning = StatusEffectRegistry.Require("burning");
        var manager = new CaveMobManager(null, null, EntityRegistry.Require(Magmaborn.EntityName));
        var drops = new DropManager();
        Assert.NotNull(manager.Spawn(Magmaborn.EntityName, OnFloor(2.5f, 8.5f, MobConfig.CaveMagmabornHeight), 0,
            spawnIn: false));

        var target = PlayerAt(8.5f, 8.5f);
        for (var i = 0; i < (int)(10f / Dt) && target.DamageTaken == 0; i++)
            manager.Update(Dt, chunks, drops, [target]);

        Assert.True(target.DamageTaken > 0, "the magmaborn never landed its blow");
        Assert.True(target.Effects!.IsActive(burning));
    }
}
