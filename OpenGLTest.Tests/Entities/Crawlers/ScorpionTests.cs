using System;
using System.Numerics;
using VoxelEngine.Content.StatusEffects;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Crawlers;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Caves;
using VoxelEngine.Status;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Caves;
using Xunit;

namespace VoxelEngine.Tests.Entities.Crawlers;

/// <summary>
/// The scorpion's own rules, driven headless: its shipped model keeps the crawler contract, it climbs a cliff to
/// its target like the spiders it shares its legs with, its first blow is the sting and only the sting poisons,
/// and it spawns in the desert's caves and on the desert's surface alone.
/// </summary>
public sealed class ScorpionTests
{
    public ScorpionTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    private static EntityType ScorpionType => EntityRegistry.Require(Scorpion.EntityName);

    private static Vector3 OnFloor(float x, float z) => new(x, 1f + ScorpionConfig.BodySize * 0.5f, z);

    private static StubTarget PlayerOn(float x, float z, float floorTop) =>
        new(StubTarget.EyeAt(new Vector3(x, floorTop, z))) { Effects = new StatusEffectSet() };

    [Fact]
    public void TheShippedModelKeepsTheCrawlerContract()
    {
        Scorpion.ValidateContent(ScorpionType);
        foreach (var leg in CrawlerRig.For(ScorpionType).Legs)
            Assert.True(Vector3.Distance(leg.Hip, leg.Rest) < leg.Reach, "a resting foot is out of its leg's reach");
    }

    [Fact]
    public void AScorpionClimbsTheCliffBetweenItAndItsTarget()
    {
        var chunks = SpiderTests.Cliff();
        var scorpion = new Scorpion(ScorpionType, OnFloor(3.5f, 8.5f), tier: 0, spawnIn: false);
        var target = PlayerOn(12.5f, 8.5f, 5f);

        var climbed = false;
        for (var i = 0; i < (int)(20f / Dt) && !Scorpion.InAttackReach(scorpion.Position, target.Eye); i++)
        {
            scorpion.Update(Dt, chunks, target);
            climbed |= scorpion.IsClinging && MathF.Abs(scorpion.SurfaceNormal.Y) < 0.5f;
        }

        Assert.True(climbed, "the scorpion never took to the cliff face");
        Assert.True(Scorpion.InAttackReach(scorpion.Position, target.Eye), "the scorpion never reached its target");
    }

    [Fact]
    public void TheStingComesFirstAndOnlyTheStingPoisons()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var venom = StatusEffectRegistry.Require("venom");
        var manager = new CaveMobManager(null, null, null, null, null, null, ScorpionType);
        var drops = new DropManager();
        Assert.NotNull(manager.Spawn(Scorpion.EntityName, OnFloor(4.5f, 8.5f), 0, spawnIn: false));

        var stung = PlayerOn(8.5f, 8.5f, 1f);
        for (var i = 0; i < (int)(10f / Dt) && stung.DamageTaken == 0; i++)
            manager.Update(Dt, chunks, drops, [stung]);
        Assert.True(stung.DamageTaken > 0, "the scorpion never landed a blow");
        Assert.True(stung.Effects!.IsActive(venom), "the first blow was not the sting");

        // The sting is on its cooldown now, so the next blow on a fresh body in the same place is the pinch
        var pinched = PlayerOn(8.5f, 8.5f, 1f);
        for (var i = 0; i < (int)(ScorpionConfig.StingCooldownSeconds * 0.8f / Dt) && pinched.DamageTaken == 0; i++)
            manager.Update(Dt, chunks, drops, [pinched]);
        Assert.True(pinched.DamageTaken > 0, "the scorpion never pinched while its sting recovered");
        Assert.False(pinched.Effects!.IsActive(venom));
    }

    [Fact]
    public void ScorpionsKeepToTheDesertAndItsCaves()
    {
        foreach (var biome in Enum.GetValues<CaveBiome>())
        {
            if (biome == CaveBiome.DesertEcho)
            {
                Assert.Equal(Scorpion.EntityName, CaveSpawnRules.KindFor(biome, 0f, spiders: true, scorpions: true));
                continue;
            }

            // Anywhere else scorpions change nothing about what a floor becomes
            foreach (var roll in new[] { 0f, 0.25f, 0.5f, 0.75f, 0.9999f })
                Assert.Equal(CaveSpawnRules.KindFor(biome, roll, spiders: true),
                    CaveSpawnRules.KindFor(biome, roll, spiders: true, scorpions: true));
        }

        foreach (var biome in Enum.GetValues<Biome>())
            Assert.Equal(biome == Biome.Desert, CaveSpawnRules.ScorpionSurfaceBiome(biome));

        // Night always takes its surface turn; the top of the roll never does by day
        Assert.True(CaveSpawnRules.ScorpionSurfaces(night: true, 0.9999f));
        Assert.False(CaveSpawnRules.ScorpionSurfaces(night: false, 0.9999f));
        Assert.True(CaveSpawnRules.ScorpionSurfaces(night: false, 0f));
    }
}
