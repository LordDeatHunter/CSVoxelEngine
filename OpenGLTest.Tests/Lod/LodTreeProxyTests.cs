using System;
using VoxelEngine.Chunks;
using VoxelEngine.Lod;
using VoxelEngine.WorldGen.Trees.Shape;
using Xunit;

namespace VoxelEngine.Tests.Lod;

/// <summary>Pure geometry rules of the tree prisms; no world or content initialization.</summary>
public sealed class LodTreeProxyTests
{
    [Theory]
    [InlineData(LodCrownProfile.Dome)]
    [InlineData(LodCrownProfile.Cone)]
    [InlineData(LodCrownProfile.Column)]
    public void AHighCrownNeverBecomesAGroundToTopLeafCurtain(LodCrownProfile profile)
    {
        var tree = new LodTreeProxy(10, 12, -20, 76, 101,
            4, 17, 6, 19, 2, 11, 12, profile);
        var trunk = tree.Trunk();
        var crown = tree.Crown();

        Assert.True(trunk.HasVolume && crown.HasVolume);
        Assert.Equal((float)tree.BaseY, trunk.MinY);
        Assert.Equal((float)tree.CrownBottom, trunk.MaxY);
        Assert.Equal((float)tree.CrownBottom, crown.MinY);
        Assert.True(crown.MinY > tree.BaseY);
        Assert.True(crown.MaxY <= tree.CrownTop);
        if (profile != LodCrownProfile.Column)
        {
            var cap = tree.Cap();
            Assert.Equal(crown.MaxY, cap.MinY);
            Assert.Equal((float)tree.CrownTop, cap.MaxY);
            Assert.True(cap.MinX > crown.MinX && cap.MaxX < crown.MaxX);
        }
    }

    [Fact]
    public void NeighboringRegionsOwnDisjointPiecesOfAnOverhangingCrown()
    {
        var crown = new LodProxyBox(59, 40, 8, 69, 47, 20);
        var left = crown.ClipXZ(0, 0, 64, 64);
        var right = crown.ClipXZ(64, 0, 128, 64);

        Assert.True(left.HasVolume && right.HasVolume);
        Assert.Equal(crown.MinX, left.MinX);
        Assert.Equal(left.MaxX, right.MinX);
        Assert.Equal(crown.MaxX, right.MaxX);
        Assert.Equal(crown.MinY, right.MinY);
        Assert.False(crown.ClipXZ(128, 0, 192, 64).HasVolume);
    }

    [Theory]
    [InlineData(63, 0, 64)]
    [InlineData(-1, -64, 0)]
    public void WideTrunkKeepsOneWorldAnchorAcrossEitherRegionSeam(int rootX, int leftEdge, int rightEdge)
    {
        const int rootZ = 8;
        var reads = 0;
        int[] Heights(int cx, int cz)
        {
            reads++;
            var map = new int[Chunk.Size * Chunk.Size];
            Array.Fill(map, 30);
            var lowX = rootX - 14;
            if (cx == lowX >> Chunk.SizeShift && cz == rootZ >> Chunk.SizeShift)
                map[((lowX & Chunk.SizeMask) << Chunk.SizeShift) | (rootZ & Chunk.SizeMask)] = 20;
            return map;
        }

        var anchor = LodTreeAnchor.Compute(rootX, rootZ, Heights, LodRegion.Levels);
        // An independently placed raw low cell at 20 under the maximum bias 6 floors to 8.
        Assert.Equal(8, anchor);
        Assert.InRange(reads, 1, 9);
        var tree = new LodTreeProxy(rootX, rootZ, anchor, 60, 70,
            rootX - 4, rootX + 5, rootZ - 4, rootZ + 5, 4, 11, 12, LodCrownProfile.Dome);
        var left = tree.Trunk().ClipXZ(leftEdge, 0, rightEdge, 64);
        var right = tree.Trunk().ClipXZ(rightEdge, 0, rightEdge + 64, 64);
        Assert.True(left.HasVolume && right.HasVolume);
        Assert.Equal(8f, left.MinY);
        Assert.Equal(left.MinY, right.MinY);
        Assert.Equal(left.MaxY, right.MaxY);
        Assert.Equal(left.MaxX, right.MinX);
    }
}
