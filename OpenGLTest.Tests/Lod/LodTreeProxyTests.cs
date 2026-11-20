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

    [Fact]
    public void LeaflessTreeHasOnlyFiniteWood()
    {
        var tree = new LodTreeProxy(0, 0, 30, 39, 39,
            0, 1, 0, 1, 1, 11, 0, LodCrownProfile.Column);
        Assert.False(tree.HasCrown);
        Assert.Equal(39f, tree.Trunk().MaxY);
    }
}
