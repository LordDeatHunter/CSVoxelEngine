using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The hit sparks: a punch, slice or thrown hit that connects throws pale chips off the mob, and a
/// client sparks the connect it predicts while its mirrored mob takes no damage.
/// </summary>
[Collection("Worldgen seed")]
public sealed class MobHitSparkTests : IDisposable
{
    private const float Dt = 1f / 60f;

    private World? _world;

    public MobHitSparkTests() => TestContent.EnsureLoaded();

    public void Dispose()
    {
        _world?.SaveAndDispose();
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
        catch (InvalidOperationException)
        {
        }
    }

    // A storeless singleplayer world with the eye high in open air facing -Z, like HitStopTests'.
    private World NewWorld(out Vector3 eye)
    {
        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, null);
        eye = new Vector3(0.5f, 300f, 0.5f);
        _world.Camera.TestTeleport(eye, yaw: -90f, pitch: 0f);
        return _world;
    }

    private static void Punch(World world)
    {
        world.UpdateBlockBreaking(Dt, isHolding: true);
        for (var i = 0; i < 60; i++)
            world.Player.Update(Dt, 0f);
    }

    [Fact]
    public void APunchThatConnectsSparksOffTheMob()
    {
        var world = NewWorld(out var eye);
        Assert.True(world.SpawnEntity("goblin", eye + new Vector3(0f, -Goblin.Height * 0.5f, -2f)));
        Assert.Empty(world.Debris);

        Punch(world);

        Assert.Equal(WorldGenerationConfig.HitSparkCount, world.Debris.Count);
        foreach (var chip in world.Debris)
        {
            Assert.Equal(DebrisLook.Color, chip.Look);
            Assert.Equal(BiomeTint.VertexTint(WorldGenerationConfig.HitSparkColor), chip.Tint);
        }
    }

    [Fact]
    public void AClientSparksOnThePunchItPredictsWillLand()
    {
        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, null, clientMode: true);
        _world.GetChunkManager().Headless = true;
        var eye = new Vector3(0.5f, 300f, 0.5f);
        _world.Camera.TestTeleport(eye, yaw: -90f, pitch: 0f);
        _world.ApplyNetworkEntities([new NetEntity(NetEntityKind.Slime, 7, eye + new Vector3(0f, 0f, -2f), 0f, 3, 0, 0,
            Vector3.Zero)]);

        Punch(_world);

        Assert.Equal(WorldGenerationConfig.HitSparkCount, _world.Debris.Count);
        // The prediction is cosmetic: the mirrored mob took no damage.
        Assert.Equal(3, _world.Slimes[0].Health);
    }

    [Fact]
    public void AMissedPunchSparksNothing()
    {
        var world = NewWorld(out _);
        Punch(world);
        Assert.Empty(world.Debris);
    }
}
