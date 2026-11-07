using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Interaction;
using Xunit;

namespace VoxelEngine.Tests.Interaction;

/// <summary>
/// Which volume a right-click means: the clicked cell lies on the volume's front face, the volume
/// extends into the clicked face, and for a 2x2x2 the four candidates are tried right-down,
/// right-up, left-down, left-up. These pin that order, the top-face case that reads the view
/// direction instead of the normal, the refusal when a candidate holds anything but the source
/// block, and a 3x3x3 found from any cell of its front.
/// </summary>
public sealed class MultiblockPlacementTests
{
    public MultiblockPlacementTests() => TestContent.EnsureLoaded();

    private static byte Bricks => ContentRegistry.RequireBlock("mud_bricks").Id;

    /// <summary>The kiln's edge length, the 2x2x2 these order rules were written for.</summary>
    private static int Two => ContentRegistry.RequireBlock("mud_brick_kiln").MultiblockSize;

    /// <summary>A block of source cells <paramref name="edge"/> wide at the origin; at 3 every
    /// 2x2x2 volume around its center fits.</summary>
    private static Dictionary<(int, int, int), byte> Cube(int edge = 3)
    {
        var cells = new Dictionary<(int, int, int), byte>();
        for (var x = 0; x < edge; x++)
        for (var y = 0; y < edge; y++)
        for (var z = 0; z < edge; z++)
            cells[(x, y, z)] = Bricks;
        return cells;
    }

    private static Func<int, int, int, byte> Reader(Dictionary<(int, int, int), byte> cells) =>
        (x, y, z) => cells.TryGetValue((x, y, z), out var id) ? id : (byte)0;

    [Fact]
    public void RightDownWinsWhenSeveralVolumesFit()
    {
        var cells = Cube();
        // Clicking the center of the cube's +Z face while looking along -Z: screen-right is +X
        // (Cross(+Y, +Z)), and the volume grows away from the camera into z = 1
        var clicked = new Vector3(1, 1, 2);
        var normal = Vector3.UnitZ;
        var forward = -Vector3.UnitZ;

        Assert.True(MultiblockPlacement.TryFindVolume(Reader(cells), Bricks, Two, clicked, normal, forward,
            out var anchor, out var facing));
        Assert.Equal(new Vector3(1, 0, 1), anchor);
        // The opening faces the player, i.e. along the face that was clicked (+Z = 1)
        Assert.Equal(1, facing);

        // Break one cell of the right-down volume and the right-up one takes over, and so on
        // down the candidate order
        cells.Remove((2, 0, 1));
        Assert.True(MultiblockPlacement.TryFindVolume(Reader(cells), Bricks, Two, clicked, normal, forward,
            out anchor, out facing));
        Assert.Equal(new Vector3(1, 1, 1), anchor);

        cells.Remove((2, 2, 1));
        Assert.True(MultiblockPlacement.TryFindVolume(Reader(cells), Bricks, Two, clicked, normal, forward,
            out anchor, out _));
        Assert.Equal(new Vector3(0, 0, 1), anchor);

        cells.Remove((0, 0, 1));
        Assert.True(MultiblockPlacement.TryFindVolume(Reader(cells), Bricks, Two, clicked, normal, forward,
            out anchor, out _));
        Assert.Equal(new Vector3(0, 1, 1), anchor);

        cells.Remove((0, 2, 1));
        Assert.False(MultiblockPlacement.TryFindVolume(Reader(cells), Bricks, Two, clicked, normal, forward,
            out _, out facing));
        Assert.True(facing < 0);
    }

    [Fact]
    public void TopFaceUsesViewDirectionAndFacesThePlayer()
    {
        var cells = Cube();
        // A top face says nothing about where the player is, so the view direction does: looking
        // mostly along -Z makes screen-up -Z, and the volume sinks into the cube (depth -Y)
        var clicked = new Vector3(1, 2, 1);

        Assert.True(MultiblockPlacement.TryFindVolume(Reader(cells), Bricks, Two, clicked, Vector3.UnitY,
            new Vector3(0f, -0.3f, -1f), out var anchor, out var facing));

        // Right-down in this frame is +X and +Z, so the anchor is the clicked cell one step down
        Assert.Equal(new Vector3(1, 1, 1), anchor);
        // The opening turns back toward the player, who stands on the +Z side
        Assert.Equal(1, facing);
    }

    [Fact]
    public void RefusesAVolumeWithAForeignCell()
    {
        var cells = Cube();
        var clicked = new Vector3(1, 1, 2);
        // The clicked cell belongs to all four candidates, so a stranger in it rules them all out
        cells[(1, 1, 2)] = ContentRegistry.RequireBlock("stone").Id;

        Assert.False(MultiblockPlacement.TryFindVolume(Reader(cells), Bricks, Two, clicked, Vector3.UnitZ,
            -Vector3.UnitZ, out _, out _));

        // An unloaded cell reads as air through the reader, and air is not the source block
        cells.Remove((1, 1, 2));
        Assert.False(MultiblockPlacement.TryFindVolume(Reader(cells), Bricks, Two, clicked, Vector3.UnitZ,
            -Vector3.UnitZ, out _, out _));
    }

    [Fact]
    public void AThreeCubeIsFoundFromEveryCellOfItsFront()
    {
        var cells = Cube();
        // Every cell of the cube's +Z face, whether a corner, an edge or the center, converts
        // the whole cube: the candidate that holds the clicked cell where it really is wins
        for (var x = 0; x < 3; x++)
        for (var y = 0; y < 3; y++)
        {
            Assert.True(MultiblockPlacement.TryFindVolume(Reader(cells), Bricks, 3, new Vector3(x, y, 2),
                Vector3.UnitZ, -Vector3.UnitZ, out var anchor, out var facing), $"front cell ({x}, {y})");
            Assert.Equal(Vector3.Zero, anchor);
            Assert.Equal(1, facing);
        }

        // One foreign cell anywhere in the cube rules it out, and a 2x2x2 still fits beside it
        cells[(0, 0, 0)] = ContentRegistry.RequireBlock("stone").Id;
        Assert.False(MultiblockPlacement.TryFindVolume(Reader(cells), Bricks, 3, new Vector3(1, 1, 2),
            Vector3.UnitZ, -Vector3.UnitZ, out _, out _));
        Assert.True(MultiblockPlacement.TryFindVolume(Reader(cells), Bricks, Two, new Vector3(1, 1, 2),
            Vector3.UnitZ, -Vector3.UnitZ, out _, out _));
    }

    [Fact]
    public void AThreeCubeMayBeHollowButItsShellAndItsCenterTakeNothingForeign()
    {
        var cells = Cube();
        var stone = ContentRegistry.RequireBlock("stone").Id;

        // The center touches no face: empty or a brick, the cube converts either way
        cells[(1, 1, 1)] = 0;
        Assert.True(MultiblockPlacement.TryFindVolume(Reader(cells), Bricks, 3, new Vector3(1, 1, 2),
            Vector3.UnitZ, -Vector3.UnitZ, out var anchor, out _));
        Assert.Equal(Vector3.Zero, anchor);

        // ...but nothing else there, and no hole in the shell
        cells[(1, 1, 1)] = stone;
        Assert.False(MultiblockPlacement.TryFindVolume(Reader(cells), Bricks, 3, new Vector3(1, 1, 2),
            Vector3.UnitZ, -Vector3.UnitZ, out _, out _));
        cells[(1, 1, 1)] = Bricks;
        cells[(1, 2, 1)] = 0;
        Assert.False(MultiblockPlacement.TryFindVolume(Reader(cells), Bricks, 3, new Vector3(1, 1, 2),
            Vector3.UnitZ, -Vector3.UnitZ, out _, out _));
    }

    [Fact]
    public void AmongSeveralThreeCubesTheRightmostThenLowestWins()
    {
        // A 5 wide, 5 tall, 3 deep slab: from its center front cell nine 3x3x3 volumes fit, and
        // the preference generalizes right-down: farthest right first, then farthest down
        var cells = new Dictionary<(int, int, int), byte>();
        for (var x = 0; x < 5; x++)
        for (var y = 0; y < 5; y++)
        for (var z = 0; z < 3; z++)
            cells[(x, y, z)] = Bricks;
        var clicked = new Vector3(2, 2, 2);

        Assert.True(MultiblockPlacement.TryFindVolume(Reader(cells), Bricks, 3, clicked, Vector3.UnitZ,
            -Vector3.UnitZ, out var anchor, out _));
        Assert.Equal(new Vector3(2, 0, 0), anchor);

        // Spoil the lowest right volume and the next one up the right column takes over
        cells.Remove((4, 0, 0));
        Assert.True(MultiblockPlacement.TryFindVolume(Reader(cells), Bricks, 3, clicked, Vector3.UnitZ,
            -Vector3.UnitZ, out anchor, out _));
        Assert.Equal(new Vector3(2, 1, 0), anchor);
    }
}
