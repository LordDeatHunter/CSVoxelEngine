using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Effects.Climate;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Goblins;
using VoxelEngine.Networking;
using VoxelEngine.Particles;
using Xunit;

namespace VoxelEngine.Tests.Particles;

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
        world.LeftClick.UpdateBlockBreaking(Dt, isHolding: true);
        for (var i = 0; i < 60; i++)
            world.Player.Update(Dt, 0f);
    }

    // A connecting hit throws the pale spark chips and, beside them, the mob's own blood. Each population is checked
    // on its own look, color and count, and together they are every chip the hit made.
    private static void AssertSparksAndGore(IReadOnlyList<BlockDebris> debris, EntityType mob)
    {
        var sparkTint = BiomeTint.VertexTint(JuiceConfig.HitSparkColor);
        var goreTint = BiomeTint.VertexTint(mob.BloodColor!.Value);
        Assert.NotEqual(sparkTint, goreTint);

        var sparks = debris.Where(chip => chip.Tint == sparkTint).ToList();
        var gore = debris.Where(chip => chip.Tint == goreTint).ToList();
        Assert.Equal(JuiceConfig.HitSparkCount, sparks.Count);
        Assert.Equal(JuiceConfig.GoreHitCount, gore.Count);
        Assert.Equal(debris.Count, sparks.Count + gore.Count);
        Assert.All(debris, chip => Assert.Equal(DebrisLook.Color, chip.Look));
    }

    [Fact]
    public void APunchThatConnectsSparksOffTheMob()
    {
        var world = NewWorld(out var eye);
        Assert.True(world.SpawnEntity("goblin", eye + new Vector3(0f, -Goblin.Height * 0.5f, -2f)));
        Assert.Empty(world.GetDebrisSystem().Debris);

        Punch(world);

        AssertSparksAndGore(world.GetDebrisSystem().Debris, EntityRegistry.Require("goblin"));
    }

    [Fact]
    public void AClientSparksOnThePunchItPredictsWillLand()
    {
        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, null, clientMode: true);
        _world.GetChunkManager().Headless = true;
        var eye = new Vector3(0.5f, 300f, 0.5f);
        _world.Camera.TestTeleport(eye, yaw: -90f, pitch: 0f);
        _world.ClientMirror.ApplyNetworkEntities([new NetEntity(NetEntityKind.Slime, 7, eye + new Vector3(0f, 0f, -2f), 0f, 3, 0, 0,
            Vector3.Zero)]);

        Punch(_world);

        AssertSparksAndGore(_world.GetDebrisSystem().Debris, _world.Slimes[0].Type);
        // The prediction is cosmetic: the mirrored mob took no damage.
        Assert.Equal(3, _world.Slimes[0].Health);
    }

    [Fact]
    public void AMissedPunchSparksNothing()
    {
        var world = NewWorld(out _);
        Punch(world);
        Assert.Empty(world.GetDebrisSystem().Debris);
    }
}
