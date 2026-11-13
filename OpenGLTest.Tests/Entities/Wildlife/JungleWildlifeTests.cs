using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.StatusEffects;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Wildlife;
using VoxelEngine.Simulation;
using VoxelEngine.Status;
using Xunit;

namespace VoxelEngine.Tests.Entities.Wildlife;

/// <summary>
/// The jungle's animals, driven headless: a corpse bloom never leaves its root, opens for a player in its wake range
/// and poisons, in its burst and in the cloud that lingers, only those who stand in reach (never the immune, never
/// itself); a giant centipede runs a noticed player down and bites only in reach, with venom.
/// </summary>
public sealed class JungleWildlifeTests
{
    public JungleWildlifeTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    private static StubTarget PlayerAt(float x, float z) =>
        new(StubTarget.EyeAt(new Vector3(x, 1f, z))) { Effects = new StatusEffectSet() };

    private static WildlifeManager NewManager() =>
        new(WildlifeSpecies.Names.Select(EntityRegistry.Require));

    private static T Standing<T>(WildlifeManager manager, string name, float x, float z) where T : Animal =>
        Assert.IsType<T>(manager.Spawn(name, new Vector3(x, 1f + WildlifeSpecies.HeightOf(name) * 0.5f, z), spawnIn: false));

    private static bool RunUntil(WildlifeManager manager, ChunkManager chunks, IReadOnlyList<IMobTarget> targets,
        float seconds, System.Func<bool> done)
    {
        var drops = new DropManager();
        for (var t = 0f; t < seconds; t += Dt)
        {
            manager.Update(Dt, chunks, drops, targets);
            if (done()) return true;
        }
        return false;
    }

    private static bool Poisoned(StubTarget target) =>
        target.Effects!.IsActive(StatusEffectRegistry.Require(CorpseBloom.PoisonEffect));

    [Fact]
    public void ABloomOpensForAPlayerInItsWakeRangeNeverMovesAndPoisonsOnlyThoseInReach()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var manager = new WildlifeManager(WildlifeSpecies.Names.Select(EntityRegistry.Require));
        var bloom = Standing<CorpseBloom>(manager, CorpseBloom.EntityName, 8f, 8f);
        var root = bloom.Position;

        // Beyond its wake range it stays shut and does nothing
        var far = PlayerAt(8f + WildlifeConfig.BloomWakeRangeBlocks + 6f, 8f);
        Assert.False(bloom.Heeds([far]));
        RunUntil(manager, chunks, [far], 3f, () => false);
        Assert.False(bloom.IsOpen);
        Assert.False(Poisoned(far));

        // A player in the wake range but outside the trigger radius opens it without being spewed on
        var wary = PlayerAt(8f + (WildlifeConfig.BloomTriggerRadiusBlocks + WildlifeConfig.BloomWakeRangeBlocks) * 0.5f, 8f);
        Assert.True(bloom.Heeds([wary]));
        RunUntil(manager, chunks, [wary], 1f, () => bloom.IsOpen);
        Assert.True(bloom.IsOpen);

        // One inside the trigger radius is spewed on and poisoned; the bloom does not move
        var near = PlayerAt(8f + WildlifeConfig.BloomTriggerRadiusBlocks * 0.5f, 8f);
        Assert.True(RunUntil(manager, chunks, [near, wary], 20f, () => Poisoned(near)), "the bloom never poisoned the player");
        Assert.False(Poisoned(wary));
        Assert.Equal(root.X, bloom.Position.X, 3);
        Assert.Equal(root.Z, bloom.Position.Z, 3);

        // An immune (flying or creative) player is never billed
        var flier = new StubTarget(near.Eye) { IsFlying = true, Effects = new StatusEffectSet() };
        RunUntil(manager, chunks, [flier], 10f, () => false);
        Assert.False(Poisoned(flier));
    }

    [Fact]
    public void ABloomsCloudKeepsPoisoningForAWhileAfterTheBurstThenThins()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var manager = NewManager();
        var bloom = Standing<CorpseBloom>(manager, CorpseBloom.EntityName, 8f, 8f);
        var player = PlayerAt(8f + 1.5f, 8f);

        Assert.True(RunUntil(manager, chunks, [player], 20f, () => bloom.CloudActive), "no cloud after the burst");
        Assert.True(CorpseBloom.InBurst(bloom.Feet, player.Eye));
        Assert.True(bloom.CloudReaches(player.Eye));
        Assert.False(bloom.CloudReaches(player.Eye + new Vector3(WildlifeConfig.BloomBurstRadiusBlocks + 2f, 0f, 0f)));

        // The cloud thins out: with nobody awake to spew again it ends
        var gone = PlayerAt(8f + WildlifeConfig.BloomWakeRangeBlocks + 10f, 8f);
        Assert.True(RunUntil(manager, chunks, [gone], WildlifeConfig.BloomCloudSeconds + 2f, () => !bloom.CloudActive));
    }

    [Fact]
    public void ACentipedeRunsDownANoticedPlayerAndBitesOnlyInReach()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var manager = NewManager();
        var centipede = Standing<Centipede>(manager, Centipede.EntityName, 8f, 8f);
        var player = PlayerAt(8f + WildlifeConfig.CentipedeNoticeRangeBlocks * 0.6f, 8f);
        var start = centipede.Position;

        Assert.True(centipede.Heeds([player]));
        Assert.True(RunUntil(manager, chunks, [player], 30f, () => player.DamageTaken > 0), "the centipede never bit the player");
        Assert.True(Vector3.Distance(start, centipede.Position) > 1f, "it bit without closing the distance");
        Assert.Equal(0, player.DamageTaken % centipede.StrikeDamage);
        Assert.True(player.Effects!.IsActive(StatusEffectRegistry.Require(Centipede.VenomEffect)));

        Assert.False(Centipede.InBiteReach(centipede.Position,
            centipede.Position + new Vector3(WildlifeConfig.CentipedeBiteReach + 3f, 0f, 0f)));
        Assert.False(centipede.Heeds([new StubTarget(player.Eye) { IsFlying = true }]));
    }

    [Fact]
    public void BothJungleAnimalsLiveOnlyInTheJungleAndTheDragonflyRoamsItWithoutWater()
    {
        foreach (var kind in new[] { WildlifeKind.CorpseBloom, WildlifeKind.Centipede })
        {
            var rule = WildlifeSpawnRules.All.Single(r => r.Kind == kind);
            Assert.Equal([VoxelEngine.WorldGen.Biome.Jungle], rule.Biomes);
        }

        var dragonfly = WildlifeSpawnRules.All.Single(r => r.Kind == WildlifeKind.Dragonfly);
        Assert.True(dragonfly.LivesIn(VoxelEngine.WorldGen.Biome.Jungle, nearWater: false));
        Assert.False(dragonfly.LivesIn(VoxelEngine.WorldGen.Biome.Forest, nearWater: false));
    }
}