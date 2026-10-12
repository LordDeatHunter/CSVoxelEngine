using System.Collections.Generic;
using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// A multiblock lives in the chunk storage as an anchor plus seven parts, with the facing and
/// each part's offset packed into the state byte. These pin that encoding, the anchor resolve
/// from every cell of the structure, the integrity check (including the "a cell is unloaded, so
/// do not judge it" case) and the batch convert/revert writes.
/// </summary>
public sealed class MultiblockTests
{
    public MultiblockTests() => TestContent.EnsureLoaded();

    private static BlockType Kiln => ContentRegistry.RequireBlock("mud_brick_kiln");
    private static BlockType Bricks => ContentRegistry.RequireBlock("mud_bricks");

    private static (ChunkManager Manager, HashSet<Vector3> Touched) ConvertedKiln(Vector3 anchor, int facing)
    {
        var (manager, _) = MeshTestWorld.WalledArea();
        var touched = new HashSet<Vector3>();
        Multiblock.Convert(manager, anchor, facing, Kiln, touched);
        return (manager, touched);
    }

    private static (byte Id, byte State) Cell(ChunkManager manager, Vector3 cell)
    {
        Assert.True(manager.TryGetCell((int)cell.X, (int)cell.Y, (int)cell.Z, out var id, out var state),
            $"cell {cell} should be loaded");
        return (id, state);
    }

    [Fact]
    public void StateRoundTripsFacingAndOffset()
    {
        for (var facing = 0; facing < 4; facing++)
        foreach (var (dx, dy, dz) in Multiblock.Offsets)
        {
            var state = Multiblock.EncodeState(facing, dx, dy, dz);

            // The mesher reads the facing straight out of the low two bits of any cell
            Assert.Equal(facing, state & 3);

            Multiblock.DecodeState(state, out var f, out var x, out var y, out var z);
            Assert.Equal((facing, dx, dy, dz), (f, x, y, z));
        }
    }

    [Fact]
    public void ConvertWritesAnchorAndPartsAndResolveFindsTheAnchorFromEveryCell()
    {
        var anchor = new Vector3(3, 3, 3);
        var (manager, touched) = ConvertedKiln(anchor, 2);

        foreach (var (dx, dy, dz) in Multiblock.Offsets)
        {
            var cell = anchor + new Vector3(dx, dy, dz);
            var (id, state) = Cell(manager, cell);
            var isAnchor = dx == 0 && dy == 0 && dz == 0;

            Assert.Equal(isAnchor ? Kiln.Id : Kiln.MultiblockPartId, id);
            Assert.Equal(Multiblock.EncodeState(2, dx, dy, dz), state);

            // Every cell of the structure resolves back to the one anchor
            Assert.True(Multiblock.TryResolveAnchor(manager, (int)cell.X, (int)cell.Y, (int)cell.Z,
                out var resolved, out var facing, out var type));
            Assert.Equal(anchor, resolved);
            Assert.Equal(2, facing);
            Assert.Same(Kiln, type);
        }

        Assert.True(Multiblock.IsIntact(manager, anchor, 2, Kiln, out var anyUnloaded));
        Assert.False(anyUnloaded);
        Assert.Equal(new HashSet<Vector3> { Vector3.Zero }, touched);
    }

    [Fact]
    public void RevertRestoresSourceAndClearsOnlyTheMinedCell()
    {
        var anchor = new Vector3(3, 3, 3);
        var (manager, _) = ConvertedKiln(anchor, 0);
        var mined = new Vector3(4, 3, 4);

        var touched = new HashSet<Vector3>();
        Multiblock.Revert(manager, anchor, Kiln, mined, touched);

        foreach (var (dx, dy, dz) in Multiblock.Offsets)
        {
            var cell = anchor + new Vector3(dx, dy, dz);
            var (id, state) = Cell(manager, cell);
            // The mined cell is the only hole; the rest are plain bricks again, state cleared
            Assert.Equal(cell == mined ? (byte)0 : Bricks.Id, id);
            Assert.Equal(0, state);
        }

        Assert.Equal(new HashSet<Vector3> { Vector3.Zero }, touched);
    }

    [Fact]
    public void IsIntactIsFalseAfterAPartIsBlasted()
    {
        var anchor = new Vector3(3, 3, 3);
        var (manager, _) = ConvertedKiln(anchor, 1);
        var blasted = anchor + new Vector3(1, 1, 0);

        // A blast clears cells straight out of the chunk, knowing nothing about structures
        manager.Chunks[Vector3.Zero].SetBlockRaw((int)blasted.X, (int)blasted.Y, (int)blasted.Z, 0, 0);

        Assert.False(Multiblock.IsIntact(manager, anchor, 1, Kiln, out var anyUnloaded));
        Assert.False(anyUnloaded);

        // The surviving parts still point at their anchor, which is what lets a sweep clean up
        var survivor = anchor + new Vector3(0, 0, 1);
        Assert.True(Multiblock.TryResolveAnchor(manager, (int)survivor.X, (int)survivor.Y, (int)survivor.Z,
            out var resolved, out _, out _));
        Assert.Equal(anchor, resolved);

        var touched = new HashSet<Vector3>();
        Multiblock.Revert(manager, anchor, Kiln, null, touched);

        foreach (var (dx, dy, dz) in Multiblock.Offsets)
        {
            var cell = anchor + new Vector3(dx, dy, dz);
            var (id, state) = Cell(manager, cell);
            Assert.Equal(cell == blasted ? (byte)0 : Bricks.Id, id);
            Assert.Equal(0, state);
        }
    }

    [Fact]
    public void RevertWithTheAnchorGoneRestoresTheSevenParts()
    {
        var anchor = new Vector3(3, 3, 3);
        var (manager, _) = ConvertedKiln(anchor, 3);
        manager.Chunks[Vector3.Zero].SetBlockRaw((int)anchor.X, (int)anchor.Y, (int)anchor.Z, 0, 0);

        // With the anchor gone a part has nothing to point at, so the parts alone can never
        // clean themselves up - the kiln manager's sweep is what calls Revert here
        var part = anchor + new Vector3(1, 0, 0);
        Assert.False(Multiblock.TryResolveAnchor(manager, (int)part.X, (int)part.Y, (int)part.Z,
            out _, out _, out _));

        var touched = new HashSet<Vector3>();
        Multiblock.Revert(manager, anchor, Kiln, null, touched);

        foreach (var (dx, dy, dz) in Multiblock.Offsets)
        {
            var cell = anchor + new Vector3(dx, dy, dz);
            var (id, state) = Cell(manager, cell);
            Assert.Equal(cell == anchor ? (byte)0 : Bricks.Id, id);
            Assert.Equal(0, state);
        }
    }

    [Fact]
    public void IsIntactReportsUnloadedInsteadOfBroken()
    {
        // The volume straddles the centre chunk's +X border
        var anchor = new Vector3(15, 3, 3);
        var (manager, _) = ConvertedKiln(anchor, 0);
        Assert.True(Multiblock.IsIntact(manager, anchor, 0, Kiln, out _));

        Assert.True(manager.Chunks.TryRemove(new Vector3(1, 0, 0), out _));

        Assert.False(Multiblock.IsIntact(manager, anchor, 0, Kiln, out var anyUnloaded));
        Assert.True(anyUnloaded);
    }
}
