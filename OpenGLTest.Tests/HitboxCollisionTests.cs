using System;
using System.Linq;
using System.Numerics;
using Silk.NET.Input;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The <c>collidesByHitbox</c> flag: a flagged block collides with its declared hitbox boxes
/// instead of the whole cell, so a low slab is stepped onto and stood on. Everything else keeps
/// the full-cube test, and the registry refuses the flag where those boxes would never be read.
/// </summary>
public sealed class HitboxCollisionTests
{
    public HitboxCollisionTests() => TestContent.EnsureLoaded();

    private const float Dt = 0.05f;

    /// <summary>One chunk with a stone floor at y=0; the caller stands blocks on top of it.</summary>
    private static ChunkManager FlatFloor(out Chunk chunk)
    {
        var manager = new ChunkManager(null!);
        chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(x, 0, z, stone, 0);
        manager.Chunks[Vector3.Zero] = chunk;
        return manager;
    }

    private static void Settle(Camera camera, ChunkManager manager, int frames = 40)
    {
        var keys = new ScriptedKeyboard();
        for (var i = 0; i < frames; i++)
            camera.Update(Dt, keys, manager);
    }

    [Fact]
    public void BlockWithoutTheFlagStillCollidesAsAFullCube()
    {
        var manager = FlatFloor(out var chunk);
        // The quern declares hitboxes only 0.625 high but never sets collidesByHitbox, so the
        // body must still rest on the cell top at y = 2, not sink onto the plate.
        chunk.SetBlockRaw(8, 1, 8, ContentRegistry.RequireBlock("quern").Id, 0);

        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(8.5f, 2.5f + Camera.EyeHeight, 8.5f), yaw: 0f, pitch: 0f);
        Settle(camera, manager);

        Assert.True(camera.IsGrounded, "did not land on the quern");
        Assert.InRange(camera.Position.Y - Camera.EyeHeight, 1.99f, 2.06f);
    }

    [Fact]
    public void RegistryRejectsTheFlagWithoutAHitboxOrOnAnIneligibleBlock()
    {
        const string path = "blocks/test.json";
        var slab = new[] { new BlockHitbox(Vector3.Zero, new Vector3(1f, 0.25f, 1f)) };
        var flagged = new BlockDefinition { CollidesByHitbox = true };

        Assert.Contains(path, Assert.Throws<InvalidOperationException>(() =>
            ContentRegistry.ResolveCollidesByHitbox(flagged, null, solid: true, fluid: false, stackMax: 0, path)).Message);
        Assert.Contains(path, Assert.Throws<InvalidOperationException>(() =>
            ContentRegistry.ResolveCollidesByHitbox(flagged, slab, solid: true, fluid: true, stackMax: 0, path)).Message);
        Assert.Contains(path, Assert.Throws<InvalidOperationException>(() =>
            ContentRegistry.ResolveCollidesByHitbox(flagged, slab, solid: true, fluid: false, stackMax: 16, path)).Message);
        Assert.Contains(path, Assert.Throws<InvalidOperationException>(() =>
            ContentRegistry.ResolveCollidesByHitbox(flagged, slab, solid: false, fluid: false, stackMax: 0, path)).Message);

        Assert.True(ContentRegistry.ResolveCollidesByHitbox(flagged, slab, solid: true, fluid: false, stackMax: 0, path));
        // The flag absent is false whatever the rest says, so no hitbox is required.
        Assert.False(ContentRegistry.ResolveCollidesByHitbox(new BlockDefinition(), null, solid: true, fluid: false, stackMax: 0, path));
    }

    [Fact]
    public void BodyDroppedOnAMoldRestsOnItsSlabTop()
    {
        var manager = FlatFloor(out var chunk);
        chunk.SetBlockRaw(8, 1, 8, ContentRegistry.RequireBlock("mold_pickaxe").Id, 0);

        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(8.5f, 2.5f + Camera.EyeHeight, 8.5f), yaw: 0f, pitch: 0f);
        Settle(camera, manager);

        // The slab top is 1.25; MoveOnAxis sweeps in CollisionStepSize increments and
        // OverlapsShape counts a touch as a hit, so the feet settle within one step of it.
        Assert.True(camera.IsGrounded, "did not land on the mold");
        Assert.InRange(camera.Position.Y - Camera.EyeHeight, 1.25f, 1.31f);
    }

    [Fact]
    public void WalkingOnFootStepsOntoAMold()
    {
        var manager = FlatFloor(out var chunk);
        var mold = ContentRegistry.RequireBlock("mold_pickaxe").Id;
        for (var x = 5; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(x, 1, z, mold, 0);

        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(2.5f, 1f + Camera.EyeHeight, 8.5f), yaw: 0f, pitch: 0f);

        var keys = new ScriptedKeyboard();
        keys.Press([Key.W]);
        for (var i = 0; i < 15; i++)
            camera.Update(Dt, keys, manager);
        keys.ReleaseAll();
        Settle(camera, manager, frames: 10);

        // A full-block step cannot be walked (CameraStepTests.WalkingDoesNotClimbAFullBlockStep),
        // so clearing this one proves the body climbed the 0.25 slab the flag exposes.
        Assert.True(camera.Position.X > 5f, $"did not step onto the mold: {camera.Position.X}");
        Assert.InRange(camera.Position.Y - Camera.EyeHeight, 1.25f, 1.31f);
    }

    [Fact]
    public void MoldLeavesTheSpaceAboveItsSlabFree()
    {
        var manager = FlatFloor(out var chunk);
        var mold = ContentRegistry.RequireBlock("mold_pickaxe").Id;
        chunk.SetBlockRaw(8, 1, 8, mold, 0);
        var stone = ContentRegistry.RequireBlock("stone").Id;
        chunk.SetBlockRaw(9, 1, 8, stone, 0);

        // The predicate Camera.IsColliding consults for a flagged id: free above the slab top,
        // blocked once the body dips into it, and a plain stone cell still blocks the high box.
        Assert.False(TreeWoodShape.OverlapsShape(mold, manager, 8, 1, 8,
            new Vector3(8.2f, 1.3f, 8.2f), new Vector3(8.8f, 1.9f, 8.8f)));
        Assert.True(TreeWoodShape.OverlapsShape(mold, manager, 8, 1, 8,
            new Vector3(8.2f, 1.1f, 8.2f), new Vector3(8.8f, 1.9f, 8.8f)));
        Assert.True(TreeWoodShape.OverlapsShape(stone, manager, 9, 1, 8,
            new Vector3(9.2f, 1.3f, 8.2f), new Vector3(9.8f, 1.9f, 8.8f)));
    }

    [Fact]
    public void PixelRimShellsCollideWithTheirRimBoxes()
    {
        var pixel = TreeSpeciesRegistry.All.First(s => s.Rim == TrunkRim.Pixel);
        var block = TreeSpeciesRegistry.All.First(s => s.Rim == TrunkRim.Block && !BlockType.HasPixelRim(s.WoodId));
        var shell = WoodState.Encode(WoodState.FullCube, 3, shell: true);

        // A box strictly inside the (-X, -Z) corner's carved row 0, and one in the solid middle
        var px = 1f / WoodState.PixelsPerBlock;
        var cornerMin = new Vector3(8.001f, 1.2f, 8.001f);
        var cornerMax = new Vector3(8f + TreeWoodShape.RimInsetPx(0) * px * 0.5f, 1.8f, 8f + px * 0.5f);
        var middleMin = new Vector3(8.4f, 1.2f, 8.4f);
        var middleMax = new Vector3(8.6f, 1.8f, 8.6f);

        // A lone pixel-rim shell has all four corners exposed: the body slips into a carved corner
        var manager = FlatFloor(out var chunk);
        chunk.SetBlockRaw(8, 1, 8, pixel.WoodId, shell);
        Assert.False(TreeWoodShape.OverlapsShape(pixel.WoodId, manager, 8, 1, 8, cornerMin, cornerMax));
        Assert.True(TreeWoodShape.OverlapsShape(pixel.WoodId, manager, 8, 1, 8, middleMin, middleMax));
        Assert.True(TreeWoodShape.GetBoxesAt(pixel.WoodId, manager, 8, 1, 8).Length > 1);

        // Rim mass on all four edges leaves nothing exposed: a plain cube again
        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            chunk.SetBlockRaw(8 + dx, 1, 8 + dz, pixel.WoodId, shell);
        Assert.True(TreeWoodShape.OverlapsShape(pixel.WoodId, manager, 8, 1, 8, cornerMin, cornerMax));
        Assert.Equal(new[] { BlockHitbox.FullCube }, TreeWoodShape.GetBoxesAt(pixel.WoodId, manager, 8, 1, 8));

        // A block-rim species' lone shell never bevels
        var other = FlatFloor(out var otherChunk);
        otherChunk.SetBlockRaw(8, 1, 8, block.WoodId, shell);
        Assert.True(TreeWoodShape.OverlapsShape(block.WoodId, other, 8, 1, 8, cornerMin, cornerMax));
        Assert.Equal(new[] { BlockHitbox.FullCube }, TreeWoodShape.GetBoxesAt(block.WoodId, other, 8, 1, 8));

        // A pixel-rim core (not a shell) never bevels either
        var core = FlatFloor(out var coreChunk);
        coreChunk.SetBlockRaw(8, 1, 8, pixel.WoodId, WoodState.Encode(WoodState.FullCube, WoodState.ParentRoot, shell: false));
        Assert.True(TreeWoodShape.OverlapsShape(pixel.WoodId, core, 8, 1, 8, cornerMin, cornerMax));
    }
}
