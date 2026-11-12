using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Chunks.Meshing;
using VoxelEngine.Content.Shapes;
using VoxelEngine.WorldGen.Trees;
using Xunit;

namespace VoxelEngine.Tests.Chunks;

/// <summary>The slit a chopped thin wood cell shows: it only removes wood, and never severs the node.</summary>
public sealed class ThinWoodBiteTests
{
    private static float Volume(BlockHitbox b) => b.Size.X * b.Size.Y * b.Size.Z;

    [Theory]
    [InlineData(0, 3, 14)]
    [InlineData(1, 7, 6)]
    [InlineData(2, 1, 1)]
    [InlineData(3, 12, 14)]
    public void TheBiteOnlyRemovesWoodThatTheCutCovers(int face, int thickness, int depthPx)
    {
        var state = WoodState.Encode(thickness, WoodState.ParentRoot, false);
        var half = TreeWoodShape.HalfWidth(thickness);
        var node = new BlockHitbox(new Vector3(0.5f - half), new Vector3(0.5f + half));
        var cut = ThinWoodBite.BiteBox(state, face, depthPx);

        var left = new List<BlockHitbox>();
        ThinWoodBite.Subtract(node, cut, left);

        var lo = Vector3.Max(node.Min, cut.Min);
        var hi = Vector3.Min(node.Max, cut.Max);
        var removed = Math.Max(hi.X - lo.X, 0f) * Math.Max(hi.Y - lo.Y, 0f) * Math.Max(hi.Z - lo.Z, 0f);
        Assert.True(removed > 0f, "a bite always takes some wood");
        Assert.Equal(Volume(node) - removed, left.Sum(Volume), 1e-5f);
        // Nothing left lies inside the cut, and everything left stays inside the node
        Assert.All(left, b =>
        {
            Assert.True(b.Min.X >= node.Min.X - 1e-5f && b.Max.X <= node.Max.X + 1e-5f);
            var overlap = Math.Max(Math.Min(b.Max.X, cut.Max.X) - Math.Max(b.Min.X, cut.Min.X), 0f)
                          * Math.Max(Math.Min(b.Max.Y, cut.Max.Y) - Math.Max(b.Min.Y, cut.Min.Y), 0f)
                          * Math.Max(Math.Min(b.Max.Z, cut.Max.Z) - Math.Max(b.Min.Z, cut.Min.Z), 0f);
            Assert.True(overlap < 1e-6f);
        });
    }

    [Fact]
    public void TheSlabIsAcrossTheWoodsRunWhateverItsDirection()
    {
        const int thick = 5;
        // A root runs up: the cut is a horizontal slab (thin in Y, the whole cell the other ways)
        var root = ThinWoodBite.BiteBox(WoodState.Encode(thick, WoodState.ParentRoot, false), 0, 8);
        Assert.True(root.Size.Y < 0.2f && root.Size.X >= 1f - 1e-5f);

        // A limb whose parent is to its -X side runs along X: the cut is a vertical slab, thin in X and
        // reaching the whole cell's height
        var limb = WoodState.Encode(thick, 2, false);
        var across = ThinWoodBite.BiteBox(limb, 0, 8);
        Assert.True(across.Size.X < 0.2f);
        Assert.Equal(1f, across.Size.Y, 1e-5f);
        // The same limb struck on a face the wood runs through falls back to a horizontal slab
        var through = ThinWoodBite.BiteBox(limb, 2, 8);
        Assert.True(through.Size.Y < 0.2f);

        // Limbs running along Z are cut across Z
        var alongZ = ThinWoodBite.BiteBox(WoodState.Encode(thick, 0, false), 2, 8);
        Assert.True(alongZ.Size.Z < 0.2f && alongZ.Size.Y >= 1f - 1e-5f);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(15)]
    public void EvenTheDeepestBiteLeavesACoreOfTheNode(int thickness)
    {
        var state = WoodState.Encode(thickness, WoodState.ParentRoot, false);
        var half = TreeWoodShape.HalfWidth(thickness);
        var cut = ThinWoodBite.BiteBox(state, 0, 14);
        // The cut from the -Z face stops short of the node's far (+Z) face
        Assert.True(cut.Max.Z < 0.5f + half - 1e-4f);
    }
}
