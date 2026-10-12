using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The goblin's notice pip: it starts on the frame the goblin first has a player to chase, runs
/// once and never restarts when the target is lost and picked up again.
/// </summary>
public sealed class GoblinNoticePipTests
{
    public GoblinNoticePipTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    private static Camera Player(Vector3 eye)
    {
        var camera = new Camera(null, null);
        camera.TestTeleport(eye, yaw: 0f, pitch: 0f);
        return camera;
    }

    [Fact]
    public void ThePipRunsOnceFromTheFirstNotice()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var player = Player(new Vector3(8.5f, 1f + Camera.EyeHeight, 12.5f));
        var goblin = new Goblin(EntityRegistry.Require("goblin"), new Vector3(8.5f, 1.5f, 8.5f),
            ammo: 0, spawnIn: false);

        // Nobody to chase: the pip is over before it ever started
        goblin.Update(Dt, chunks, null);
        Assert.Equal(1f, goblin.NoticePipProgress);

        // The first sight of the player starts it at exactly 0
        goblin.Update(Dt, chunks, player);
        Assert.Equal(0f, goblin.NoticePipProgress);

        var midway = 0f;
        for (var i = 0; i < 120; i++)
        {
            goblin.Update(Dt, chunks, player);
            if (i == 10) midway = goblin.NoticePipProgress;
        }

        Assert.True(midway > 0f && midway < 1f, $"the pip did not run: {midway}");
        Assert.Equal(1f, goblin.NoticePipProgress);

        // Losing the target and picking it up again never re-pops the pip
        goblin.Update(Dt, chunks, null);
        goblin.Update(Dt, chunks, player);
        Assert.Equal(1f, goblin.NoticePipProgress);
    }

    [Fact]
    public void ADyingGoblinNeverPopsThePip()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var player = Player(new Vector3(8.5f, 1f + Camera.EyeHeight, 12.5f));
        var goblin = new Goblin(EntityRegistry.Require("goblin"), new Vector3(8.5f, 1.5f, 8.5f),
            ammo: 0, spawnIn: false);
        goblin.BeginDying();

        goblin.Update(Dt, chunks, player);
        Assert.Equal(1f, goblin.NoticePipProgress);
    }
}
