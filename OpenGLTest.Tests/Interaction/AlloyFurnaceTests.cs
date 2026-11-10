using System;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Interaction;
using Xunit;

namespace VoxelEngine.Tests.Interaction;

/// <summary>
/// The alloy furnace in a live world: a 3x3x3 cube of alloy bricks, hollow or solid, converts
/// into the furnace through the world's convert path, and mining any shell cell reverts it.
/// Nothing is made or lost on the way: a brick built into the center comes back at the
/// conversion, and the revert leaves the center empty.
/// </summary>
[Collection("Worldgen seed")]
public sealed class AlloyFurnaceTests : IDisposable
{
    private SupportTestWorld? _world;

    public AlloyFurnaceTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
    }

    public void Dispose() => _world?.Dispose();

    private static BlockType Furnace => ContentRegistry.RequireBlock("alloy_furnace");

    /// <summary>Alloy bricks in the cells and on the ground as drops.</summary>
    private int BricksInWorld(Vector3 anchor, int size, ushort bricks)
    {
        var cells = 0;
        foreach (var (dx, dy, dz) in Multiblock.Offsets(size))
            if (_world!.IdAt((int)anchor.X + dx, (int)anchor.Y + dy, (int)anchor.Z + dz) == bricks)
                cells++;
        return cells + _world!.World.Drops.Count(drop => drop.BlockId == bricks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACubeOfAlloyBricksConvertsAndMiningAShellCellRevertsIt(bool solidCenter)
    {
        _world = new SupportTestWorld();
        var furnace = Furnace;
        var size = furnace.MultiblockSize;
        var bricks = furnace.MultiblockSourceId;
        var anchor = new Vector3(2, _world.Top + 1, 2);
        _world.Load(3, _world.Top + 2, 3);

        foreach (var (dx, dy, dz) in Multiblock.Offsets(size))
        {
            if (Multiblock.IsInterior(dx, dy, dz, size) && !solidCenter) continue;
            _world.Set((int)anchor.X + dx, (int)anchor.Y + dy, (int)anchor.Z + dz, bricks);
        }
        var built = BricksInWorld(anchor, size, bricks);

        // A click on the middle of the +Z face, looking north at it, from the player's side
        var clicked = anchor + new Vector3(1, 1, size - 1);
        Assert.True(_world.World.Edits.TryConvertMultiblock(clicked, Vector3.UnitZ, bricks, furnace.Id,
            -Vector3.UnitZ));

        foreach (var (dx, dy, dz) in Multiblock.Offsets(size))
        {
            var (x, y, z) = ((int)anchor.X + dx, (int)anchor.Y + dy, (int)anchor.Z + dz);
            var isAnchor = dx == 0 && dy == 0 && dz == 0;
            Assert.Equal(isAnchor ? furnace.Id : furnace.MultiblockPartId, _world.IdAt(x, y, z));
            Assert.True(Multiblock.TryResolveAnchor(_world.Manager, x, y, z, out var resolved, out var facing, out _));
            Assert.Equal(anchor, resolved);
            // The opening faces the clicked side
            Assert.Equal(Vector3.UnitZ, MultiblockShape.FrontNormal(facing));
        }
        // The furnace stands for its shell; a brick built into the center came back as a drop
        var shell = Multiblock.Offsets(size).ToArray().Count(o => !Multiblock.IsInterior(o.X, o.Y, o.Z, size));
        Assert.Equal(built - shell, _world.World.Drops.Count(drop => drop.BlockId == bricks));

        // Mining a shell cell takes the furnace down: the shell is bricks again, the center empty
        var mined = anchor + new Vector3(size - 1, 0, size - 1);
        Assert.True(_world.Break((int)mined.X, (int)mined.Y, (int)mined.Z));
        foreach (var (dx, dy, dz) in Multiblock.Offsets(size))
        {
            var cell = anchor + new Vector3(dx, dy, dz);
            var expected = cell == mined || Multiblock.IsInterior(dx, dy, dz, size) ? BlockType.Air : bricks;
            Assert.Equal(expected, _world.IdAt((int)cell.X, (int)cell.Y, (int)cell.Z));
            Assert.Equal(0, _world.StateAt((int)cell.X, (int)cell.Y, (int)cell.Z));
        }
        Assert.Equal(built, BricksInWorld(anchor, size, bricks));
    }
}
