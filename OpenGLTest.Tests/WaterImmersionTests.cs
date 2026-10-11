using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class WaterImmersionTests
{
    public WaterImmersionTests() => TestContent.EnsureLoaded();

    private const float Dt = 0.02f;

    // A pool over the whole chunk: stone floor at y = 0, still source water from y = 1 up to
    // y = depth (its surface 8/9 into the top cell).
    private static ChunkManager Pool(int depth)
    {
        var manager = new ChunkManager(null!);
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var water = ContentRegistry.RequireBlock("water").Id;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            chunk.SetBlockRaw(x, 0, z, stone, 0);
            for (var y = 1; y <= depth && y < Chunk.Size; y++)
                chunk.SetBlockRaw(x, y, z, water, 0);
        }
        manager.Chunks[Vector3.Zero] = chunk;
        return manager;
    }

    // One physics frame with no keys held, for a body whose feet stand at feetY.
    private static Camera BodyAt(ChunkManager manager, float feetY)
    {
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(8.5f, feetY + Camera.EyeHeight, 8.5f), yaw: 0f, pitch: 0f);
        camera.Update(Dt, new ScriptedKeyboard(), manager);
        return camera;
    }

    [Fact]
    public void ThePhaseNeverGetsShallowerAsTheWaterRises()
    {
        const float feet = 0f;
        var previousShare = 0f;
        var previousPhase = WaterPhase.Dry;
        var seen = new System.Collections.Generic.HashSet<WaterPhase>();
        for (var surface = -0.5f; surface <= 2.5f; surface += 0.01f)
        {
            var share = WaterImmersion.Share(feet, surface);
            var phase = WaterImmersion.PhaseOf(share, eyeUnder: surface > feet + Camera.EyeHeight);
            Assert.InRange(share, 0f, 1f);
            Assert.True(share >= previousShare);
            Assert.True(phase >= previousPhase, $"{phase} after {previousPhase} at surface {surface}");
            previousShare = share;
            previousPhase = phase;
            seen.Add(phase);
        }

        // All four bands show up along the way, shallowest first
        Assert.Equal(4, seen.Count);
        Assert.Equal(0f, WaterImmersion.Share(feet, float.NegativeInfinity));
    }

    [Fact]
    public void TheBuoyancyAlwaysHeadsBackToTheRestShare()
    {
        const float rest = WorldGenerationConfig.WaterFloatRestShare;
        Assert.Equal(0f, WaterImmersion.BuoyancySpeed(rest), 5);

        var previous = float.NegativeInfinity;
        for (var share = 0f; share <= 1.0001f; share += 0.01f)
        {
            var speed = WaterImmersion.BuoyancySpeed(share);
            // Deeper never pushes up less, and the push always points back at the rest
            Assert.True(speed >= previous);
            if (share < rest - 1e-3f) Assert.True(speed < 0f);
            if (share > rest + 1e-3f) Assert.True(speed > 0f);
            Assert.InRange(speed, PlayerPhysics.SinkSpeed, PlayerPhysics.SwimUpSpeed);
            previous = speed;
        }
    }

    [Fact]
    public void AOneBlockSourcePoolIsWadedThrough()
    {
        var camera = BodyAt(Pool(depth: 1), feetY: 1f);
        Assert.True(camera.IsInWater);
        Assert.Equal(WaterPhase.Wade, camera.WaterPhase);
        Assert.InRange(camera.ImmersionShare, 0.01f, WorldGenerationConfig.WaterFloatShare);
    }

    [Fact]
    public void DeepWaterFloatsABodyWithItsEyeOutAndSwimsOneWithItUnder()
    {
        var manager = Pool(depth: 12);
        var surface = 12f + FluidState.SourceHeight;

        // The eye a hair above the surface, the rest of the body under
        var floating = BodyAt(manager, surface - Camera.EyeHeight + 0.05f);
        Assert.Equal(WaterPhase.Float, floating.WaterPhase);
        Assert.True(floating.IsInWater);
        Assert.False(floating.IsEyeInWater);

        var under = BodyAt(manager, surface - 4f);
        Assert.Equal(WaterPhase.Swim, under.WaterPhase);
        Assert.Equal(1f, under.ImmersionShare);

        var dry = BodyAt(Pool(depth: 0), feetY: 1f);
        Assert.Equal(WaterPhase.Dry, dry.WaterPhase);
        Assert.False(dry.IsInWater);
    }
}
