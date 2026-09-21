using System;
using System.Collections.Generic;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Which 2x2x2 volume a right-click means: the clicked cell is a corner, the volume extends into
/// the clicked face, and the four candidates are tried right-down, right-up, left-down, left-up.
/// These pin that order, the top-face case that reads the view direction instead of the normal,
/// and the refusal when a candidate holds anything but the source block.
/// </summary>
public sealed class MultiblockPlacementTests
{
    public MultiblockPlacementTests() => TestContent.EnsureLoaded();

    private static byte Bricks => ContentRegistry.RequireBlock("mud_bricks").Id;

    /// <summary>A 3x3x3 block of source cells at the origin, every volume around its centre fits.</summary>
    private static Dictionary<(int, int, int), byte> Cube()
    {
        var cells = new Dictionary<(int, int, int), byte>();
        for (var x = 0; x < 3; x++)
        for (var y = 0; y < 3; y++)
        for (var z = 0; z < 3; z++)
            cells[(x, y, z)] = Bricks;
        return cells;
    }

    private static Func<int, int, int, byte> Reader(Dictionary<(int, int, int), byte> cells) =>
        (x, y, z) => cells.TryGetValue((x, y, z), out var id) ? id : (byte)0;

    [Fact]
    public void RightDownWinsWhenSeveralVolumesFit()
    {
        var cells = Cube();
        // Clicking the centre of the cube's +Z face while looking along -Z: screen-right is +X
        // (Cross(+Y, +Z)), and the volume grows away from the camera into z = 1
        var clicked = new Vector3(1, 1, 2);
        var normal = Vector3.UnitZ;
        var forward = -Vector3.UnitZ;

        Assert.True(MultiblockPlacement.TryFindVolume(Reader(cells), Bricks, clicked, normal, forward,
            out var anchor, out var facing));
        Assert.Equal(new Vector3(1, 0, 1), anchor);
        // The opening faces the player, i.e. along the face that was clicked (+Z = 1)
        Assert.Equal(1, facing);

        // Break one cell of the right-down volume and the right-up one takes over, and so on
        // down the candidate order
        cells.Remove((2, 0, 1));
        Assert.True(MultiblockPlacement.TryFindVolume(Reader(cells), Bricks, clicked, normal, forward,
            out anchor, out facing));
        Assert.Equal(new Vector3(1, 1, 1), anchor);

        cells.Remove((2, 2, 1));
        Assert.True(MultiblockPlacement.TryFindVolume(Reader(cells), Bricks, clicked, normal, forward,
            out anchor, out _));
        Assert.Equal(new Vector3(0, 0, 1), anchor);

        cells.Remove((0, 0, 1));
        Assert.True(MultiblockPlacement.TryFindVolume(Reader(cells), Bricks, clicked, normal, forward,
            out anchor, out _));
        Assert.Equal(new Vector3(0, 1, 1), anchor);

        cells.Remove((0, 2, 1));
        Assert.False(MultiblockPlacement.TryFindVolume(Reader(cells), Bricks, clicked, normal, forward,
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

        Assert.True(MultiblockPlacement.TryFindVolume(Reader(cells), Bricks, clicked, Vector3.UnitY,
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

        Assert.False(MultiblockPlacement.TryFindVolume(Reader(cells), Bricks, clicked, Vector3.UnitZ,
            -Vector3.UnitZ, out _, out _));

        // An unloaded cell reads as air through the reader, and air is not the source block
        cells.Remove((1, 1, 2));
        Assert.False(MultiblockPlacement.TryFindVolume(Reader(cells), Bricks, clicked, Vector3.UnitZ,
            -Vector3.UnitZ, out _, out _));
    }
}
