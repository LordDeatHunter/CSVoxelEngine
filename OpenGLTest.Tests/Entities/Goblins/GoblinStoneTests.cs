using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Player;
using VoxelEngine.Simulation;
using Xunit;

namespace VoxelEngine.Tests.Entities.Goblins;

/// <summary>
/// Goblin stones hitting mobs: a stone in flight strikes a goblin or a slime in its way (never its
/// own thrower) for the stone's damage, while a stone that misses shatters on the ground as before.
/// </summary>
public sealed class GoblinStoneTests
{
    public GoblinStoneTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    private static Camera Player(Vector3 eye)
    {
        var camera = new Camera(null, null);
        camera.TestTeleport(eye, yaw: 0f, pitch: 0f);
        return camera;
    }

    [Fact]
    public void AStoneStrikesASlimeInItsWayButNeverItsOwnThrower()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var camera = Player(new Vector3(8.5f, 1f + Camera.EyeHeight, 14.5f));

        var goblins = new GoblinManager(EntityRegistry.Require("goblin"));
        var thrower = goblins.Spawn(new Vector3(2.5f, 1f + Goblin.Height * 0.5f, 8.5f), ammo: 0)!;
        var slimes = new SlimeManager(EntityRegistry.Require("slime"));
        var slime = slimes.Spawn(new Vector3(4.5f, 1f + Slime.HalfExtentFor(1), 8.5f))!;
        var before = slime.Health;

        // A stone from the thrower straight down +X into the slime's body, level with its center
        goblins.TestThrowStone(thrower, new Vector3(3.5f, 1.25f, 8.5f), new Vector3(14f, 0f, 0f));
        for (var i = 0; i < 120 && slimes.Slimes.Count > 0 && slime.Health == before; i++)
            goblins.Update(Dt, chunks, new DropManager(), camera, slimes: slimes.Slimes);

        Assert.True(slime.Health < before, "the stone never struck the slime");
        Assert.Equal(before - WorldGenerationConfig.GoblinStoneDamage, slime.Health);
        Assert.Empty(goblins.Stones);

        // A stone thrown straight at its own thrower's cell passes through it
        var ownBefore = thrower.Health;
        goblins.TestThrowStone(thrower, new Vector3(2.5f, 1.2f, 8.5f), new Vector3(0.01f, 0f, 0f));
        for (var i = 0; i < 30; i++)
            goblins.Update(Dt, chunks, new DropManager(), camera, slimes: slimes.Slimes);
        Assert.Equal(ownBefore, thrower.Health);
    }

    [Fact]
    public void AStoneThatMissesShattersOnTheGround()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var camera = Player(new Vector3(8.5f, 1f + Camera.EyeHeight, 14.5f));
        var goblins = new GoblinManager(EntityRegistry.Require("goblin"));
        var thrower = goblins.Spawn(new Vector3(2.5f, 1f + Goblin.Height * 0.5f, 8.5f), ammo: 0)!;

        goblins.TestThrowStone(thrower, new Vector3(3.5f, 1.2f, 8.5f), new Vector3(14f, 0f, 0f));
        for (var i = 0; i < 300 && goblins.Stones.Count > 0; i++)
            goblins.Update(Dt, chunks, new DropManager(), camera);

        Assert.Empty(goblins.Stones);
    }
}
