using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Interaction;
using Xunit;

namespace VoxelEngine.Tests.Interaction;

/// <summary>
/// A multiblock lives in the chunk storage as an anchor plus its parts, with the facing and each
/// part's offset packed into the state byte. These pin that encoding, the anchor resolve from
/// every cell of the structure, the integrity check (including the "a cell is unloaded, so do
/// not judge it" case) and the batch convert/revert writes, for the shipped 2x2x2 kiln and for a
/// 3x3x3 structure.
/// </summary>
public sealed class MultiblockTests
{
    public MultiblockTests() => TestContent.EnsureLoaded();

    private static BlockType Kiln => ContentRegistry.RequireBlock("mud_brick_kiln");
    private static BlockType Bricks => ContentRegistry.RequireBlock("mud_bricks");

    /// <summary>
    /// A 3x3x3 anchor no shipped block is: the kiln's own anchor, part and source ids with a
    /// bigger volume. Only the structure rules read it, and the resolve gets it through
    /// <see cref="TypesWith"/> instead of the registry.
    /// </summary>
    private static BlockType SizeThree()
    {
        var kiln = Kiln;
        var type = new BlockType
        {
            Id = kiln.Id,
            Name = "test_size_three",
            DisplayName = "Test Size Three",
            Renderer = BlockRenderer.Multiblock,
            MultiblockSize = 3,
        };
        type.MultiblockPartId = kiln.MultiblockPartId;
        type.MultiblockSourceId = kiln.MultiblockSourceId;
        return type;
    }

    /// <summary>The registry's id table with <paramref name="anchorType"/> in its id's slot.</summary>
    private static BlockType?[] TypesWith(BlockType anchorType)
    {
        var types = (BlockType?[])BlockType.ById.Clone();
        types[anchorType.Id] = anchorType;
        return types;
    }

    public static TheoryData<int> Sizes => new() { 2, 3 };

    private static BlockType AnchorOfSize(int size) => size == Kiln.MultiblockSize ? Kiln : SizeThree();

    private static (ChunkManager Manager, HashSet<Vector3> Touched) Converted(BlockType anchorType,
        Vector3 anchor, int facing)
    {
        var (manager, _) = MeshTestWorld.WalledArea();
        var touched = new HashSet<Vector3>();
        Multiblock.Convert(manager, anchor, facing, anchorType, touched);
        return (manager, touched);
    }

    private static (byte Id, byte State) Cell(ChunkManager manager, Vector3 cell)
    {
        Assert.True(manager.TryGetCell((int)cell.X, (int)cell.Y, (int)cell.Z, out var id, out var state),
            $"cell {cell} should be loaded");
        return (id, state);
    }

    [Fact]
    public void StateRoundTripsFacingAndOffsetForEveryLegalSize()
    {
        for (var size = MultiblockShape.MinSize; size <= MultiblockShape.MaxSize; size++)
        {
            var seen = new HashSet<byte>();
            for (var facing = 0; facing < 4; facing++)
            foreach (var (dx, dy, dz) in Multiblock.Offsets(size))
            {
                var state = Multiblock.EncodeState(facing, dx, dy, dz);

                // The mesher reads the facing straight out of the low two bits of any cell
                Assert.Equal(facing, state & 3);
                // Every cell and facing gets its own byte, so a cell's offset is never ambiguous
                Assert.True(seen.Add(state), $"state {state} repeats at size {size}");

                Multiblock.DecodeState(state, out var f, out var x, out var y, out var z);
                Assert.Equal((facing, dx, dy, dz), (f, x, y, z));
            }
        }
    }

    [Fact]
    public void TheAnchorLeadsEveryVolumeAndTheOffsetsFillItExactlyOnce()
    {
        for (var size = MultiblockShape.MinSize; size <= MultiblockShape.MaxSize; size++)
        {
            var offsets = Multiblock.Offsets(size);
            Assert.Equal((0, 0, 0), offsets[0]);

            var cells = new HashSet<(int, int, int)>();
            foreach (var (dx, dy, dz) in offsets)
            {
                Assert.InRange(dx, 0, size - 1);
                Assert.InRange(dy, 0, size - 1);
                Assert.InRange(dz, 0, size - 1);
                Assert.True(cells.Add((dx, dy, dz)));
            }
            Assert.Equal(size * size * size, cells.Count);
        }
    }

    [Fact]
    public void ABiggerStructureOnlyAddsHighBitsSoTheKilnsBytesAreUnchanged()
    {
        // A 2x2x2 cell's offsets are 0 or 1, so its byte is the old facing | dx<<2 | dy<<3 | dz<<4
        foreach (var (dx, dy, dz) in Multiblock.Offsets(Kiln.MultiblockSize))
            Assert.Equal(3 | (dx << 2) | (dy << 3) | (dz << 4), Multiblock.EncodeState(3, dx, dy, dz));
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void ConvertWritesAnchorAndPartsAndResolveFindsTheAnchorFromEveryCell(int size)
    {
        var anchorType = AnchorOfSize(size);
        var types = TypesWith(anchorType);
        var anchor = new Vector3(3, 3, 3);
        var (manager, touched) = Converted(anchorType, anchor, 2);

        foreach (var (dx, dy, dz) in Multiblock.Offsets(size))
        {
            var cell = anchor + new Vector3(dx, dy, dz);
            var (id, state) = Cell(manager, cell);
            var isAnchor = dx == 0 && dy == 0 && dz == 0;

            Assert.Equal(isAnchor ? anchorType.Id : anchorType.MultiblockPartId, id);
            Assert.Equal(Multiblock.EncodeState(2, dx, dy, dz), state);

            // Every cell of the structure resolves back to the one anchor
            Assert.True(Multiblock.TryResolveAnchor(manager, (int)cell.X, (int)cell.Y, (int)cell.Z, types,
                out var resolved, out var facing, out var type));
            Assert.Equal(anchor, resolved);
            Assert.Equal(2, facing);
            Assert.Same(anchorType, type);
        }

        // Nothing outside the volume was written
        var beyond = anchor + new Vector3(size, 0, 0);
        Assert.Equal(0, Cell(manager, beyond).Id);

        Assert.True(Multiblock.IsIntact(manager, anchor, 2, anchorType, out var anyUnloaded));
        Assert.False(anyUnloaded);
        Assert.Equal(new HashSet<Vector3> { Vector3.Zero }, touched);
    }

    [Fact]
    public void APartReachingPastItsAnchorsVolumeDoesNotResolve()
    {
        // A 3x3x3 structure's far parts carry offsets of 2; read against the 2x2x2 kiln (the
        // same ids) they point at an anchor too small to own them, which is a stranger
        var anchor = new Vector3(3, 3, 3);
        var (manager, _) = Converted(SizeThree(), anchor, 0);

        var far = anchor + new Vector3(2, 2, 2);
        Assert.False(Multiblock.TryResolveAnchor(manager, (int)far.X, (int)far.Y, (int)far.Z,
            out _, out _, out _));
        var near = anchor + new Vector3(1, 1, 1);
        Assert.True(Multiblock.TryResolveAnchor(manager, (int)near.X, (int)near.Y, (int)near.Z,
            out var resolved, out _, out _));
        Assert.Equal(anchor, resolved);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void RevertRestoresSourceAndClearsOnlyTheMinedCell(int size)
    {
        var anchorType = AnchorOfSize(size);
        var anchor = new Vector3(3, 3, 3);
        var (manager, _) = Converted(anchorType, anchor, 0);
        var mined = anchor + new Vector3(size - 1, 0, size - 1);

        var touched = new HashSet<Vector3>();
        Multiblock.Revert(manager, anchor, anchorType, mined, touched);

        foreach (var (dx, dy, dz) in Multiblock.Offsets(size))
        {
            var cell = anchor + new Vector3(dx, dy, dz);
            var (id, state) = Cell(manager, cell);
            // The mined cell is the only hole; the rest are plain bricks again, state cleared
            Assert.Equal(cell == mined ? (byte)0 : Bricks.Id, id);
            Assert.Equal(0, state);
        }

        Assert.Equal(new HashSet<Vector3> { Vector3.Zero }, touched);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void IsIntactIsFalseAfterAPartIsBlasted(int size)
    {
        var anchorType = AnchorOfSize(size);
        var types = TypesWith(anchorType);
        var anchor = new Vector3(3, 3, 3);
        var (manager, _) = Converted(anchorType, anchor, 1);
        var blasted = anchor + new Vector3(size - 1, size - 1, 0);

        // A blast clears cells straight out of the chunk, knowing nothing about structures
        manager.Chunks[Vector3.Zero].SetBlockRaw((int)blasted.X, (int)blasted.Y, (int)blasted.Z, 0, 0);

        Assert.False(Multiblock.IsIntact(manager, anchor, 1, anchorType, out var anyUnloaded));
        Assert.False(anyUnloaded);

        // The surviving parts still point at their anchor, which is what lets a sweep clean up
        var survivor = anchor + new Vector3(0, 0, size - 1);
        Assert.True(Multiblock.TryResolveAnchor(manager, (int)survivor.X, (int)survivor.Y, (int)survivor.Z, types,
            out var resolved, out _, out _));
        Assert.Equal(anchor, resolved);

        var touched = new HashSet<Vector3>();
        Multiblock.Revert(manager, anchor, anchorType, null, touched);

        foreach (var (dx, dy, dz) in Multiblock.Offsets(size))
        {
            var cell = anchor + new Vector3(dx, dy, dz);
            var (id, state) = Cell(manager, cell);
            Assert.Equal(cell == blasted ? (byte)0 : Bricks.Id, id);
            Assert.Equal(0, state);
        }
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void RevertWithTheAnchorGoneRestoresEveryPart(int size)
    {
        var anchorType = AnchorOfSize(size);
        var types = TypesWith(anchorType);
        var anchor = new Vector3(3, 3, 3);
        var (manager, _) = Converted(anchorType, anchor, 3);
        manager.Chunks[Vector3.Zero].SetBlockRaw((int)anchor.X, (int)anchor.Y, (int)anchor.Z, 0, 0);

        // With the anchor gone a part has nothing to point at, so the parts alone can never
        // clean themselves up - the kiln manager's sweep is what calls Revert here
        var part = anchor + new Vector3(1, 0, 0);
        Assert.False(Multiblock.TryResolveAnchor(manager, (int)part.X, (int)part.Y, (int)part.Z, types,
            out _, out _, out _));

        var touched = new HashSet<Vector3>();
        Multiblock.Revert(manager, anchor, anchorType, null, touched);

        foreach (var (dx, dy, dz) in Multiblock.Offsets(size))
        {
            var cell = anchor + new Vector3(dx, dy, dz);
            var (id, state) = Cell(manager, cell);
            Assert.Equal(cell == anchor ? (byte)0 : Bricks.Id, id);
            Assert.Equal(0, state);
        }
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void IsIntactReportsUnloadedInsteadOfBroken(int size)
    {
        var anchorType = AnchorOfSize(size);
        // The volume straddles the center chunk's +X border
        var anchor = new Vector3(Chunk.Size - 1, 3, 3);
        var (manager, _) = Converted(anchorType, anchor, 0);
        Assert.True(Multiblock.IsIntact(manager, anchor, 0, anchorType, out _));

        Assert.True(manager.Chunks.TryRemove(new Vector3(1, 0, 0), out _));

        Assert.False(Multiblock.IsIntact(manager, anchor, 0, anchorType, out var anyUnloaded));
        Assert.True(anyUnloaded);
    }
}
