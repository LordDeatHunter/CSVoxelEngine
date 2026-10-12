using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The goblin's flee (<see cref="Goblin.IsFleeing"/>): hurt below
/// <see cref="WorldGenerationConfig.GoblinFleeHealthShare"/> of its pool it turns and runs from
/// the player instead of closing in, and it never throws while running.
/// </summary>
public sealed class GoblinFleeTests
{
    public GoblinFleeTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    private static Camera Player(Vector3 eye)
    {
        var camera = new Camera(null, null);
        camera.TestTeleport(eye, yaw: 0f, pitch: 0f);
        return camera;
    }

    // Runs the goblin for a second and reports the distance to the player at the end.
    private static float Run(Goblin goblin, Camera player, ChunkManager chunks)
    {
        IMobTarget target = player;
        for (var i = 0; i < 60; i++)
            goblin.Update(Dt, chunks, target);
        var to = new Vector3(target.Eye.X - goblin.Position.X, 0f, target.Eye.Z - goblin.Position.Z);
        return to.Length();
    }

    [Fact]
    public void AHurtGoblinRunsFromThePlayerInsteadOfClosingIn()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        // Past GoblinThrowMaxRange, so a healthy goblin closes in rather than holding the throw band
        var player = Player(new Vector3(8.5f, 1f + Camera.EyeHeight, 24.5f));
        IMobTarget target = player;

        var healthy = new Goblin(EntityRegistry.Require("goblin"), new Vector3(8.5f, 1f + Goblin.Height * 0.5f, 8.5f),
            ammo: 3, spawnIn: false);
        Assert.False(healthy.IsFleeing);
        var before = new Vector3(target.Eye.X - healthy.Position.X, 0f, target.Eye.Z - healthy.Position.Z).Length();
        var afterHealthy = Run(healthy, player, chunks);
        Assert.True(afterHealthy < before, $"a healthy goblin did not close in: {before} -> {afterHealthy}");

        var hurt = new Goblin(EntityRegistry.Require("goblin"), new Vector3(8.5f, 1f + Goblin.Height * 0.5f, 8.5f),
            ammo: 3, spawnIn: false);
        hurt.TakeDamage(hurt.MaxHealth - 1);
        Assert.True(hurt.IsFleeing, "a goblin at 1 HP is not fleeing");
        var ammoBefore = hurt.Ammo;
        var start = new Vector3(target.Eye.X - hurt.Position.X, 0f, target.Eye.Z - hurt.Position.Z).Length();
        var afterHurt = Run(hurt, player, chunks);
        Assert.True(afterHurt > start, $"a hurt goblin did not run: {start} -> {afterHurt}");
        Assert.Equal(ammoBefore, hurt.Ammo);
    }

    [Fact]
    public void TheFleeLineSitsAtTheConfiguredShareOfThePool()
    {
        var goblin = new Goblin(EntityRegistry.Require("goblin"), Vector3.Zero, ammo: 0, spawnIn: false);
        var max = goblin.MaxHealth;
        goblin.TakeDamage(max - (int)(max * WorldGenerationConfig.GoblinFleeHealthShare) - 1);
        Assert.False(goblin.IsFleeing, "a goblin above the share is already fleeing");
        goblin.TakeDamage(1);
        Assert.True(goblin.IsFleeing, "a goblin at the share is not fleeing");
    }
}
