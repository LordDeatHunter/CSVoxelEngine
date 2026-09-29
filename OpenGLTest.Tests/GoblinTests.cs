using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The goblin's own AI and physics, driven headless: it closes to the throw band, throws only
/// with a clear line (one stone per release event), closes and swings once out of stones, is
/// stopped by friction after a shove and floats in water.
/// </summary>
public sealed class GoblinTests
{
    public GoblinTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    private static EntityType Type => EntityRegistry.Require("goblin");

    private static ChunkManager StoneFloor() => MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);

    // Body centre of a goblin standing on the y = 0 floor.
    private static Vector3 GroundBody(float x, float z) => new(x, 1f + Goblin.Height * 0.5f, z);

    // A camera whose eye stands on the y = 0 floor at (x, z).
    private static Camera PlayerAt(float x, float z, float eyeY = 1f + Camera.EyeHeight)
    {
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(x, eyeY, z), yaw: 0f, pitch: 0f);
        return camera;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    [Fact]
    public void OutOfRangeGoblinWalksTowardThePlayer()
    {
        var chunks = StoneFloor();
        var goblin = new Goblin(Type, GroundBody(1.5f, 8.5f), ammo: 3);
        var player = PlayerAt(1.5f + WorldGenerationConfig.GoblinThrowMaxRange + 2f, 8.5f);
        var start = HorizontalDistance(goblin.Position, player.Position);

        for (var i = 0; i < 600 && HorizontalDistance(goblin.Position, player.Position) > start - 1f; i++)
            goblin.Update(Dt, chunks, player);

        Assert.True(HorizontalDistance(goblin.Position, player.Position) <= start - 1f);
    }

    [Fact]
    public void ThrowReleasesOnceAndSpendsOneStone()
    {
        var chunks = StoneFloor();
        const int startAmmo = 5;
        var goblin = new Goblin(Type, GroundBody(2.5f, 8.5f), startAmmo);
        var band = (WorldGenerationConfig.GoblinThrowMinRange + WorldGenerationConfig.GoblinThrowMaxRange) * 0.5f;
        var player = PlayerAt(2.5f + band, 8.5f);

        var starts = 0;
        var releases = 0;
        var releasesThisThrow = 0;
        var wasThrowing = false;
        for (var i = 0; i < (int)(20f / Dt) && releases < 3; i++)
        {
            goblin.Update(Dt, chunks, player);
            var throwing = goblin.Animation.IsPlaying(Goblin.ThrowClip);
            if (throwing && !wasThrowing)
            {
                starts++;
                releasesThisThrow = 0;
            }
            wasThrowing = throwing;

            var ammoBefore = goblin.Ammo;
            if (!goblin.TryTakeThrowRelease(out _, out _)) continue;

            releases++;
            releasesThisThrow++;
            Assert.True(throwing, "the stone left the hand after the throw clip had ended");
            Assert.Equal(1, releasesThisThrow);
            Assert.Equal(ammoBefore - 1, goblin.Ammo);
        }

        Assert.Equal(3, releases);
        Assert.Equal(releases, starts);
        Assert.Equal(startAmmo - releases, goblin.Ammo);
    }

    [Fact]
    public void NoThrowWithoutLineOfSight()
    {
        var chunks = StoneFloor();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        const int wallX = 6;
        // Three cells high: more than the auto-jump's v^2/2g apex can clear.
        for (var y = 1; y <= 3; y++)
        for (var z = 0; z < Chunk.Size; z++)
            chunks.Chunks[Vector3.Zero].SetBlockRaw(wallX, y, z, stone, 0);

        var goblin = new Goblin(Type, GroundBody(2.5f, 8.5f), ammo: 3);
        var player = PlayerAt(10.5f, 8.5f);
        var startGap = wallX - goblin.Position.X;

        for (var i = 0; i < (int)(5f / Dt); i++)
        {
            goblin.Update(Dt, chunks, player);
            Assert.False(goblin.Animation.IsPlaying(Goblin.ThrowClip), $"threw through the wall at frame {i}");
            Assert.False(goblin.TryTakeThrowRelease(out _, out _));
        }

        Assert.Equal(3, goblin.Ammo);
        Assert.True(wallX - goblin.Position.X < startGap - 0.5f,
            $"the goblin did not walk toward the wall: {goblin.Position.X}");
    }

    [Fact]
    public void EmptyHandedGoblinClosesAndSwingsOnItsCooldown()
    {
        var chunks = StoneFloor();
        var goblin = new Goblin(Type, GroundBody(2.5f, 8.5f), ammo: 0);
        var player = PlayerAt(8.5f, 8.5f);

        var firstImpact = -1;
        var secondImpact = -1;
        for (var i = 0; i < (int)(10f / Dt) && secondImpact < 0; i++)
        {
            goblin.Update(Dt, chunks, player);
            if (!goblin.TryTakeMeleeImpact()) continue;

            Assert.True(Goblin.InMeleeReach(goblin.Position, player.Position), "swung from out of reach");
            if (firstImpact < 0) firstImpact = i;
            else secondImpact = i;
        }

        Assert.True(firstImpact >= 0, "the goblin never swung");
        Assert.True(secondImpact >= 0, "the goblin swung only once");
        Assert.True(secondImpact - firstImpact >= (int)(WorldGenerationConfig.GoblinMeleeCooldownSeconds / Dt) - 1,
            $"swings {secondImpact - firstImpact} frames apart");
    }

    [Fact]
    public void ShoveSlidesThenStops()
    {
        var chunks = StoneFloor();
        var goblin = new Goblin(Type, GroundBody(8.5f, 8.5f), ammo: 0);
        // Straight overhead: nothing to walk toward and nothing in swing reach.
        var player = PlayerAt(8.5f, 8.5f, eyeY: 30f);

        for (var i = 0; i < 30; i++)
            goblin.Update(Dt, chunks, player);
        Assert.True(goblin.IsGrounded);

        var before = goblin.Position.X;
        goblin.Shove(Vector3.UnitX, WorldGenerationConfig.GoblinShoveSpeed);
        for (var i = 0; i < 6; i++)
            goblin.Update(Dt, chunks, player);
        Assert.True(goblin.Position.X > before + 0.02f);

        var settled = false;
        var previous = goblin.Position;
        for (var i = 0; i < (int)(2f / Dt) && !settled; i++)
        {
            goblin.Update(Dt, chunks, player);
            settled = Vector3.Distance(goblin.Position, previous) < 1e-3f;
            previous = goblin.Position;
        }

        Assert.True(settled, "the shove never came to rest");
    }

    [Fact]
    public void GoblinFloatsUpToTheWaterSurface()
    {
        var chunks = MobTestWorld.Pool(waterCells: 5);
        var goblin = new Goblin(Type, GroundBody(8.5f, 8.5f), ammo: 0);
        var player = PlayerAt(8.5f, 8.5f, eyeY: 30f);

        for (var i = 0; i < 600; i++)
            goblin.Update(Dt, chunks, player);

        // The top source (y = 5) surfaces near y = 6, so a buoyant goblin bobs there instead of
        // standing on the floor it spawned on.
        var feet = goblin.Position.Y - Goblin.Height * 0.5f;
        Assert.InRange(feet, 5.5f, 6.3f);
        Assert.False(goblin.IsGrounded);
    }
}
