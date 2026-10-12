using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The two global mob caps (<see cref="MobCaps"/>): the ring spawners of every kind stop at the
/// shared natural cap, a forced spawn passes it, nothing of any kind passes the hard cap, and a
/// mob playing its death counts toward neither.
/// </summary>
public sealed class MobCapTests
{
    public MobCapTests() => TestContent.EnsureLoaded();

    private static readonly Vector3 SlimeBody = new(0.5f, 1f + Slime.HalfExtent, 0.5f);
    private static readonly Vector3 GoblinBody = new(0.5f, 1f + Goblin.Height * 0.5f, 0.5f);

    private static (MobCaps caps, SlimeManager slimes, GoblinManager goblins) SharedManagers()
    {
        var caps = new MobCaps();
        return (caps, new SlimeManager(EntityRegistry.Require("slime"), caps),
            new GoblinManager(EntityRegistry.Require("goblin"), caps));
    }

    private static Camera HighCamera()
    {
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(0.5f, 100f, 0.5f), yaw: 0f, pitch: 0f);
        return camera;
    }

    // Runs the slimes' ring spawner through enough intervals that it would surely have placed one
    // on the surface world had the cap allowed it.
    private static void RunSlimeRing(SlimeManager slimes, ChunkManager surface, Camera camera, int intervals = 40)
    {
        var drops = new DropManager();
        for (var i = 0; i < intervals && slimes.Slimes.Count == 0; i++)
            slimes.Update(WorldGenerationConfig.SlimeSpawnIntervalSeconds + 0.01f, surface, drops, camera);
    }

    [Fact]
    public void ThePlainManagersCountEachOtherAgainstTheNaturalCap()
    {
        var (caps, slimes, goblins) = SharedManagers();
        // Goblins alone fill the natural cap, so the slimes' ring finds no room either
        for (var i = 0; i < WorldGenerationConfig.MobNaturalCap; i++)
            Assert.NotNull(goblins.Spawn(GoblinBody, ammo: 0));

        Assert.False(caps.AllowsNaturalSpawn);
        RunSlimeRing(slimes, MobTestWorld.Surface(), HighCamera());
        Assert.Empty(slimes.Slimes);
    }

    [Fact]
    public void ADyingMobFreesItsPlaceUnderTheNaturalCap()
    {
        var (caps, slimes, goblins) = SharedManagers();
        for (var i = 0; i < WorldGenerationConfig.MobNaturalCap; i++)
            goblins.Spawn(GoblinBody, ammo: 0);

        // Still in the goblin list (it is drawn until its death has played) but no longer counted
        goblins.Goblins[0].BeginDying();
        Assert.Equal(WorldGenerationConfig.MobNaturalCap, goblins.Goblins.Count);
        Assert.True(caps.AllowsNaturalSpawn);

        RunSlimeRing(slimes, MobTestWorld.Surface(), HighCamera());
        Assert.Single(slimes.Slimes);
        Assert.False(caps.AllowsNaturalSpawn);
    }

    [Fact]
    public void AForcedSpawnPassesTheNaturalCapButNoKindPassesTheHardCap()
    {
        var (caps, slimes, goblins) = SharedManagers();
        for (var i = 0; i < WorldGenerationConfig.MobNaturalCap; i++)
            slimes.Spawn(SlimeBody);

        Assert.NotNull(goblins.Spawn(GoblinBody, ammo: 0));
        Assert.True(caps.Living > WorldGenerationConfig.MobNaturalCap);

        // Fill the rest with both kinds in turn
        for (var i = 0; i < 2 * WorldGenerationConfig.MobHardCap && caps.ForcedRoom > 0; i++)
        {
            if (i % 2 == 0) slimes.Spawn(SlimeBody);
            else goblins.Spawn(GoblinBody, ammo: 0);
        }

        Assert.Equal(WorldGenerationConfig.MobHardCap, slimes.LivingCount + goblins.LivingCount);
        Assert.Null(slimes.Spawn(SlimeBody));
        Assert.Null(goblins.Spawn(GoblinBody, ammo: 0));
        Assert.Equal(WorldGenerationConfig.MobHardCap, caps.Living);

        // One death frees exactly one place, for whichever kind takes it
        slimes.Slimes[0].BeginDying();
        Assert.NotNull(goblins.Spawn(GoblinBody, ammo: 0));
        Assert.Null(slimes.Spawn(SlimeBody));
        Assert.Equal(WorldGenerationConfig.MobHardCap, caps.Living);
    }
}
