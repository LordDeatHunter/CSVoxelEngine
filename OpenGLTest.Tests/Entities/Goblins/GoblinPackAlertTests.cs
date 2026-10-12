using System;
using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The goblin pack alert: a goblin that notices a player pulls every other goblin within
/// <see cref="WorldGenerationConfig.GoblinPackAlertRadiusBlocks"/> forward, so they skip their
/// first-throw delay and throw sooner; one beyond the radius keeps waiting.
/// </summary>
public sealed class GoblinPackAlertTests
{
    public GoblinPackAlertTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    private static Camera Player(Vector3 eye)
    {
        var camera = new Camera(null, null);
        camera.TestTeleport(eye, yaw: 0f, pitch: 0f);
        return camera;
    }

    // Runs the goblin until its throw's release event fires and reports the frame it did.
    private static int FramesUntilRelease(Goblin goblin, Camera player, ChunkManager chunks)
    {
        for (var i = 0; i < 600; i++)
        {
            goblin.Update(Dt, chunks, player);
            if (goblin.TryTakeThrowRelease(out _, out _)) return i;
        }
        return -1;
    }

    [Fact]
    public void AnAlertedGoblinThrowsSooner()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var player = Player(new Vector3(8.5f, 1f + Camera.EyeHeight, 8.5f));

        var alerted = new Goblin(EntityRegistry.Require("goblin"), new Vector3(12.5f, 1f + Goblin.Height * 0.5f, 2.5f),
            ammo: 3, spawnIn: false);
        alerted.AlertPack();
        var alertedFrames = FramesUntilRelease(alerted, player, chunks);

        var waiting = new Goblin(EntityRegistry.Require("goblin"), new Vector3(12.5f, 1f + Goblin.Height * 0.5f, 2.5f),
            ammo: 3, spawnIn: false);
        var waitingFrames = FramesUntilRelease(waiting, player, chunks);

        Assert.True(alertedFrames >= 0 && waitingFrames >= 0, "a goblin never threw");
        Assert.True(alertedFrames < waitingFrames,
            $"an alerted goblin did not throw sooner: {alertedFrames} vs {waitingFrames}");
    }

    [Fact]
    public void OnlyThePackWithinTheRadiusIsAlerted()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var player = Player(new Vector3(8.5f, 1f + Camera.EyeHeight, 8.5f));
        var goblins = new GoblinManager(EntityRegistry.Require("goblin"));
        var noticed = goblins.Spawn(new Vector3(2.5f, 1f + Goblin.Height * 0.5f, 2.5f), ammo: 0)!;
        var near = goblins.Spawn(new Vector3(12.5f, 1f + Goblin.Height * 0.5f, 2.5f), ammo: 3)!;
        var far = goblins.Spawn(new Vector3(2.5f, 1f + Goblin.Height * 0.5f, 15.5f), ammo: 3)!;
        Assert.True(MathF.Abs(near.Position.X - noticed.Position.X) <= WorldGenerationConfig.GoblinPackAlertRadiusBlocks);
        Assert.True(MathF.Abs(far.Position.Z - noticed.Position.Z) > WorldGenerationConfig.GoblinPackAlertRadiusBlocks);

        goblins.AlertPackAround(noticed);

        var nearFrames = FramesUntilRelease(near, player, chunks);
        var farFrames = FramesUntilRelease(far, player, chunks);
        Assert.True(nearFrames >= 0 && farFrames >= 0, "a goblin never threw");
        Assert.True(nearFrames < farFrames, $"the pack alert ignored its radius: {nearFrames} vs {farFrames}");
    }

    [Fact]
    public void ANoticeIsTakenOnce()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var player = Player(new Vector3(8.5f, 1f + Camera.EyeHeight, 12.5f));
        var goblin = new Goblin(EntityRegistry.Require("goblin"), new Vector3(8.5f, 1f + Goblin.Height * 0.5f, 8.5f),
            ammo: 0, spawnIn: false);

        goblin.Update(Dt, chunks, null);
        Assert.False(goblin.TakeJustNoticed());
        goblin.Update(Dt, chunks, player);
        Assert.True(goblin.TakeJustNoticed());
        Assert.False(goblin.TakeJustNoticed());
        goblin.Update(Dt, chunks, player);
        Assert.False(goblin.TakeJustNoticed());
    }
}
