using System;
using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The goblin's dodge: a charged throw aimed at it (inside
/// <see cref="WorldGenerationConfig.GoblinDodgeConeDegrees"/> and within
/// <see cref="WorldGenerationConfig.GoblinDodgeRangeBlocks"/>) makes it sidestep out of the line
/// instead of closing in, while an aim past it is ignored.
/// </summary>
public sealed class GoblinDodgeTests
{
    public GoblinDodgeTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    private static Camera Player(Vector3 eye)
    {
        var camera = new Camera(null, null);
        camera.TestTeleport(eye, yaw: 0f, pitch: 0f);
        return camera;
    }

    [Fact]
    public void TheConeOnlyReadsThrowsAimedAtTheGoblin()
    {
        var origin = Vector3.Zero;
        var goblin = new Vector3(0f, 0f, 10f);
        Assert.True(GoblinManager.IsAimedAt(goblin, origin, Vector3.UnitZ));

        // A quarter turn off, and ~27 degrees off (past the cone)
        Assert.False(GoblinManager.IsAimedAt(goblin, origin, Vector3.UnitX));
        Assert.False(GoblinManager.IsAimedAt(goblin, origin, Vector3.Normalize(Vector3.UnitZ + new Vector3(0.5f, 0f, 0f))));

        // Out of range, and a target on top of the origin
        Assert.False(GoblinManager.IsAimedAt(new Vector3(0f, 0f, WorldGenerationConfig.GoblinDodgeRangeBlocks + 1f),
            origin, Vector3.UnitZ));
        Assert.False(GoblinManager.IsAimedAt(origin, origin, Vector3.UnitZ));
    }

    [Fact]
    public void AChargedThrowAimedAtItMakesTheGoblinSidestep()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var player = Player(new Vector3(8.5f, 1f + Camera.EyeHeight, 4.5f));
        var goblin = new Goblin(EntityRegistry.Require("goblin"), new Vector3(8.5f, 1f + Goblin.Height * 0.5f, 8.5f),
            ammo: 0, spawnIn: false);

        var startX = goblin.Position.X;
        var startZ = goblin.Position.Z;
        for (var i = 0; i < 30; i++)
            goblin.Update(Dt, chunks, player, null, dodgeAimedThrow: true);

        Assert.True(MathF.Abs(goblin.Position.X - startX) > 0.2f, "the goblin did not sidestep");
        Assert.True(MathF.Abs(goblin.Position.Z - startZ) < 0.2f, "the goblin left its line instead of sidestepping");
    }

    [Fact]
    public void WithoutTheAimItStillClosesIn()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var player = Player(new Vector3(8.5f, 1f + Camera.EyeHeight, 24.5f));
        var goblin = new Goblin(EntityRegistry.Require("goblin"), new Vector3(8.5f, 1f + Goblin.Height * 0.5f, 8.5f),
            ammo: 0, spawnIn: false);

        var before = MathF.Abs(player.Position.Z - goblin.Position.Z);
        for (var i = 0; i < 30; i++)
            goblin.Update(Dt, chunks, player);
        Assert.True(MathF.Abs(player.Position.Z - goblin.Position.Z) < before, "the goblin did not close in");
    }
}
