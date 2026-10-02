using System.Numerics;
using Silk.NET.Input;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class BlockMovementTests
{
    public BlockMovementTests() => TestContent.EnsureLoaded();

    private const float Dt = 0.05f;
    private const int Frames = 20;

    // One chunk: a stone floor at y=0, plus a body-height field of `fill` from x=4 on
    // (air means no field). The player starts at x=2.5 on the floor facing +X.
    private static ChunkManager Field(byte fill, out Camera camera)
    {
        var manager = new ChunkManager(null!);
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(x, 0, z, stone, 0);
        if (fill != BlockType.Air)
        {
            for (var x = 4; x < Chunk.Size; x++)
            for (var y = 1; y <= 3; y++)
            for (var z = 0; z < Chunk.Size; z++)
                chunk.SetBlockRaw(x, y, z, fill, 0);
        }
        manager.Chunks[Vector3.Zero] = chunk;

        camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(2.5f, 1f + Camera.EyeHeight, 8.5f), yaw: 0f, pitch: 0f);
        return manager;
    }

    private static void RunForward(Camera camera, ChunkManager manager)
    {
        var keys = new ScriptedKeyboard();
        keys.Press([Key.W]);
        for (var i = 0; i < Frames; i++)
            camera.Update(Dt, keys, manager);
        keys.ReleaseAll();
    }

    [Fact]
    public void IceFrictionIsBelowTheDefaultAndMirroredById()
    {
        var stone = ContentRegistry.RequireBlock("stone");
        var ice = ContentRegistry.RequireBlock("ice");
        Assert.Equal(BlockType.DefaultFriction, stone.Friction);
        Assert.True(ice.Friction < BlockType.DefaultFriction, $"ice friction {ice.Friction} is not below default");
        Assert.Equal(ice.Friction, BlockType.FrictionOf(ice.Id));
        Assert.Equal(BlockType.DefaultFriction, BlockType.FrictionOf(stone.Id));
    }

    [Fact]
    public void GroundAccelRateIsSlowerOnIce()
    {
        var stone = Camera.GroundAccelRate(BlockType.DefaultFriction);
        var ice = Camera.GroundAccelRate(ContentRegistry.RequireBlock("ice").Friction);
        Assert.True(ice < stone, $"ice rate {ice} should be below stone rate {stone}");
    }

    [Fact]
    public void WalkAcceleratesGraduallyFromRest()
    {
        var manager = Field(BlockType.Air, out var camera);
        var idle = new ScriptedKeyboard();
        camera.Update(Dt, idle, manager);
        camera.Update(Dt, idle, manager);

        var keys = new ScriptedKeyboard();
        keys.Press([Key.W]);
        var before = camera.Position.X;
        camera.Update(Dt, keys, manager);
        keys.ReleaseAll();

        var instantStep = 8f * Dt;
        var firstStep = camera.Position.X - before;
        Assert.True(firstStep > 0f, "no movement on the first input frame");
        Assert.True(firstStep < instantStep, $"first step {firstStep} did not ramp (instant is {instantStep})");
        Assert.InRange(camera.HorizontalVelocity.Length(), 0.01f, 8f);
    }

    [Fact]
    public void IceGlidesFurtherThanStoneAfterRelease()
    {
        static float GlideAfterRelease(byte floorId)
        {
            var manager = new ChunkManager(null!);
            var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
            chunk.IsBlockDataReady = true;
            for (var x = 0; x < Chunk.Size; x++)
            for (var z = 0; z < Chunk.Size; z++)
                chunk.SetBlockRaw(x, 0, z, floorId, 0);
            manager.Chunks[Vector3.Zero] = chunk;

            var camera = new Camera(null, null);
            camera.TestTeleport(new Vector3(2.5f, 1f + Camera.EyeHeight, 8.5f), yaw: 0f, pitch: 0f);
            var keys = new ScriptedKeyboard();
            keys.Press([Key.W]);
            for (var i = 0; i < Frames; i++)
                camera.Update(Dt, keys, manager);
            keys.ReleaseAll();
            var releaseX = camera.Position.X;
            var idle = new ScriptedKeyboard();
            for (var i = 0; i < Frames; i++)
                camera.Update(Dt, idle, manager);
            return camera.Position.X - releaseX;
        }

        var stoneGlide = GlideAfterRelease(ContentRegistry.RequireBlock("stone").Id);
        var iceGlide = GlideAfterRelease(ContentRegistry.RequireBlock("ice").Id);
        Assert.True(stoneGlide > 0f, "stone did not glide at all");
        Assert.True(iceGlide > stoneGlide + 0.5f, $"ice glide {iceGlide} not clearly beyond stone {stoneGlide}");
    }

    [Fact]
    public void LeavesAreNonBlockingAtAPartialWalkSpeed()
    {
        var leaves = ContentRegistry.RequireBlock("oak_leaves");
        Assert.InRange(leaves.MovementSpeed, 0.001f, 0.999f);
        Assert.Equal(leaves.MovementSpeed, BlockType.MovementSpeedOf(leaves.Id));
        Assert.Equal(0f, ContentRegistry.RequireBlock("stone").MovementSpeed);
    }

    [Fact]
    public void StoneStopsTheWalk()
    {
        var manager = Field(ContentRegistry.RequireBlock("stone").Id, out var camera);

        RunForward(camera, manager);

        Assert.True(camera.Position.X < 4f, $"walked through stone to x={camera.Position.X}");
    }

    [Fact]
    public void LeavesAreWalkedThroughAtReducedSpeed()
    {
        var open = Field(BlockType.Air, out var free);
        RunForward(free, open);

        var leaves = Field(ContentRegistry.RequireBlock("oak_leaves").Id, out var slowed);
        RunForward(slowed, leaves);

        Assert.True(slowed.Position.X > 4f, $"leaves blocked the walk at x={slowed.Position.X}");
        Assert.True(slowed.Position.X < free.Position.X - 1f,
            $"leaves did not slow the walk: {slowed.Position.X} vs {free.Position.X}");
    }

    [Fact]
    public void SprintClimbsStepWithLeavesInFront()
    {
        var manager = new ChunkManager(null!);
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(x, x < 5 ? 0 : 1, z, stone, 0);
        chunk.SetBlockRaw(4, 1, 8, ContentRegistry.RequireBlock("oak_leaves").Id, 0);
        manager.Chunks[Vector3.Zero] = chunk;

        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(2.5f, 1f + Camera.EyeHeight, 8.5f), yaw: 0f, pitch: 0f);
        var keys = new ScriptedKeyboard();
        keys.Press([Key.W, Key.ShiftLeft]);
        for (var i = 0; i < 15; i++)
            camera.Update(Dt, keys, manager);
        keys.ReleaseAll();

        Assert.True(camera.Position.X > 5f, $"did not clear the step: {camera.Position.X}");
        Assert.InRange(camera.Position.Y - Camera.EyeHeight, 1.99f, 2.01f);
    }

    [Fact]
    public void SprintClimbsStepWithLeavesOnTop()
    {
        var manager = new ChunkManager(null!);
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var leaves = ContentRegistry.RequireBlock("oak_leaves").Id;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(x, x < 5 ? 0 : 1, z, stone, 0);
        for (var x = 5; x < Chunk.Size; x++)
        for (var y = 2; y <= 3; y++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(x, y, z, leaves, 0);
        manager.Chunks[Vector3.Zero] = chunk;

        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(2.5f, 1f + Camera.EyeHeight, 8.5f), yaw: 0f, pitch: 0f);
        var keys = new ScriptedKeyboard();
        keys.Press([Key.W, Key.ShiftLeft]);
        for (var i = 0; i < 20; i++)
            camera.Update(Dt, keys, manager);
        keys.ReleaseAll();

        Assert.True(camera.Position.X > 5f, $"did not clear the step: {camera.Position.X}");
        Assert.InRange(camera.Position.Y - Camera.EyeHeight, 1.99f, 2.01f);
    }
}
