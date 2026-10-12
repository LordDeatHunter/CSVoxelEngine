using System.Numerics;
using VoxelEngine.Effects.Climate;
using VoxelEngine.Entities;
using VoxelEngine.Particles;
using VoxelEngine.Player;
using VoxelEngine.Simulation;
using Xunit;

namespace VoxelEngine.Tests.Particles;

/// <summary>
/// The mob death poof (<see cref="GroundDust.DeathPoof"/>): a mob whose death animation ends bursts
/// into chips of its own skin color, and a manager with nothing to draw them into (a headless
/// server) spawns none.
/// </summary>
public sealed class MobDeathPoofTests
{
    public MobDeathPoofTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    private static Camera MakeCamera()
    {
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(8.5f, 1f + Camera.EyeHeight, 2.5f), yaw: 0f, pitch: 0f);
        return camera;
    }

    [Fact]
    public void AKilledMobsFinishedDeathBurstsSkinColouredChips()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var camera = MakeCamera();
        var body = new Vector3(8.5f, 1f + Goblin.Height * 0.5f, 8.5f);

        var debris = new BlockDebrisSystem();
        var goblins = new GoblinManager(EntityRegistry.Require("goblin"));
        var goblin = goblins.Spawn(body)!;
        goblin.TakeDamage(goblin.MaxHealth);

        // The death plays out first, throwing nothing
        goblins.Update(Dt, chunks, new DropManager(), camera, debris);
        Assert.Empty(debris.Debris);

        for (var i = 0; i < 240 && goblins.Goblins.Count > 0; i++)
            goblins.Update(Dt, chunks, new DropManager(), camera, debris);

        Assert.Empty(goblins.Goblins);
        Assert.Equal(WorldGenerationConfig.MobDeathPoofCount, debris.Debris.Count);
        foreach (var chip in debris.Debris)
            Assert.Equal(DebrisLook.Color, chip.Look);

        // The chips wear the goblin's own mean skin color, not the fallback
        var skin = LandingDust.SkinColour(EntityRegistry.Require("goblin").Model, Vector3.Zero);
        Assert.True(skin != Vector3.Zero, "the goblin model has no skin to read");
        Assert.Equal(BiomeTint.VertexTint(skin), debris.Debris[0].Tint);
    }

    [Fact]
    public void AFinishedDeathOnAClientMirrorPopsToo()
    {
        var debris = new BlockDebrisSystem();
        var slimes = new SlimeManager(EntityRegistry.Require("slime"));
        var slime = slimes.Spawn(new Vector3(8.5f, 1.5f, 8.5f))!;
        slime.BeginDying();

        for (var i = 0; i < 240 && slimes.Slimes.Count > 0; i++)
            slimes.AnimateMirrors(Dt, debris);

        Assert.Empty(slimes.Slimes);
        Assert.Equal(WorldGenerationConfig.MobDeathPoofCount, debris.Debris.Count);
    }

    [Fact]
    public void AManagerWithNothingToDrawIntoThrowsNoChips()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var camera = MakeCamera();
        var goblins = new GoblinManager(EntityRegistry.Require("goblin"));
        var goblin = goblins.Spawn(new Vector3(8.5f, 1f + Goblin.Height * 0.5f, 8.5f))!;
        goblin.TakeDamage(goblin.MaxHealth);

        for (var i = 0; i < 240 && goblins.Goblins.Count > 0; i++)
            goblins.Update(Dt, chunks, new DropManager(), camera);

        Assert.Empty(goblins.Goblins);
    }
}
