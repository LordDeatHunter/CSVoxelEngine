using System;
using VoxelEngine.WorldGen.Trees;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Trees;

public sealed class WoodStateTests
{
    [Fact]
    public void RoundTripsEveryFieldCombination()
    {
        for (var thickness = 0; thickness <= WoodState.MaxThickness; thickness++)
        for (var parent = 0; parent <= WoodState.ParentMask; parent++)
        foreach (var shell in new[] { false, true })
        {
            var s = WoodState.Encode(thickness, parent, shell);
            Assert.Equal(thickness, WoodState.Thickness(s));
            Assert.Equal(parent, WoodState.Parent(s));
            Assert.Equal(shell, WoodState.IsShell(s));
            Assert.Equal(thickness + 1, WoodState.DiameterPx(s));
            Assert.Equal(thickness == WoodState.MaxThickness, WoodState.IsFullCube(s));
            Assert.Equal(parent == WoodState.ParentRoot, WoodState.IsRoot(s));
            Assert.Equal(parent == WoodState.ParentDetached, WoodState.IsDetached(s));
            Assert.Equal(!WoodState.IsRoot(s) && !WoodState.IsDetached(s), WoodState.HasParentCell(s));
        }
    }

    [Fact]
    public void FieldsAreIndependent()
    {
        for (var thickness = 0; thickness <= WoodState.MaxThickness; thickness++)
        for (var parent = 0; parent <= WoodState.ParentMask; parent++)
        foreach (var shell in new[] { false, true })
        {
            var s = WoodState.Encode(thickness, parent, shell);

            var t = WoodState.WithThickness(s, WoodState.MaxThickness - thickness);
            Assert.Equal(WoodState.MaxThickness - thickness, WoodState.Thickness(t));
            Assert.Equal(parent, WoodState.Parent(t));
            Assert.Equal(shell, WoodState.IsShell(t));

            var p = WoodState.WithParent(s, WoodState.ParentMask - parent);
            Assert.Equal(WoodState.ParentMask - parent, WoodState.Parent(p));
            Assert.Equal(thickness, WoodState.Thickness(p));
            Assert.Equal(shell, WoodState.IsShell(p));

            var sh = WoodState.WithShell(s, !shell);
            Assert.Equal(!shell, WoodState.IsShell(sh));
            Assert.Equal(thickness, WoodState.Thickness(sh));
            Assert.Equal(parent, WoodState.Parent(sh));

            Assert.Equal(s, WoodState.WithThickness(s, WoodState.Thickness(s)));
            Assert.Equal(s, WoodState.WithParent(s, WoodState.Parent(s)));
            Assert.Equal(s, WoodState.WithShell(s, WoodState.IsShell(s)));
        }
    }

    [Fact]
    public void ParentOffsetFollowsCubeFaces()
    {
        for (var dir = 0; dir < Chunk.CubeFaces.Length; dir++)
        {
            var s = WoodState.Encode(3, dir, false);
            Assert.True(WoodState.TryParentOffset(s, out var dx, out var dy, out var dz));

            var n = Chunk.CubeFaces[dir].normal;
            Assert.Equal((int)MathF.Round(n.X), dx);
            Assert.Equal((int)MathF.Round(n.Y), dy);
            Assert.Equal((int)MathF.Round(n.Z), dz);

            var nonZero = (dx != 0 ? 1 : 0) + (dy != 0 ? 1 : 0) + (dz != 0 ? 1 : 0);
            Assert.Equal(1, nonZero);
            Assert.Equal(1, Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz));
        }

        foreach (var parent in new[] { WoodState.ParentRoot, WoodState.ParentDetached })
        {
            var s = WoodState.Encode(3, parent, true);
            Assert.False(WoodState.TryParentOffset(s, out var dx, out var dy, out var dz));
            Assert.Equal((0, 0, 0), (dx, dy, dz));
        }
    }

    [Fact]
    public void DetachedWoodIsAFullCubeWithNoParent()
    {
        var s = WoodState.Detached;
        Assert.True(WoodState.IsDetached(s));
        Assert.True(WoodState.IsFullCube(s));
        Assert.False(WoodState.IsShell(s));
        Assert.False(WoodState.HasParentCell(s));
    }

    [Fact]
    public void OutOfRangeIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WoodState.Encode(WoodState.MaxThickness + 1, 0, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => WoodState.Encode(-1, 0, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => WoodState.Encode(0, WoodState.ParentMask + 1, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => WoodState.Encode(0, -1, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => WoodState.WithThickness(0, WoodState.MaxThickness + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => WoodState.WithParent(0, WoodState.ParentMask + 1));
    }
}
