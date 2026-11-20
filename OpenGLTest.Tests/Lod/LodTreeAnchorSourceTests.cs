using System;
using VoxelEngine.Chunks;
using VoxelEngine.Lod;
using Xunit;

namespace VoxelEngine.Tests.Lod;

/// <summary>Effective source heights and edit influence, with no world or content load.</summary>
public sealed class LodTreeAnchorSourceTests
{
    [Theory]
    [InlineData(63, 64)]
    [InlineData(-1, 0)]
    public void SuppliedNeighborLowersGeneratedRootsCommonSeamAnchor(int rootX, int suppliedX)
    {
        const int rootZ = 8;
        var sourceReads = 0;
        LodTreeAnchor.HeightColumn Column(int cx, int cz)
        {
            sourceReads++;
            var heights = new int[Chunk.Size * Chunk.Size];
            Array.Fill(heights, 30);
            if (cx == suppliedX >> Chunk.SizeShift && cz == rootZ >> Chunk.SizeShift)
                heights[((suppliedX & Chunk.SizeMask) << Chunk.SizeShift) | (rootZ & Chunk.SizeMask)] = 20;
            return new LodTreeAnchor.HeightColumn(heights, null, null);
        }

        var leftAnchor = LodTreeAnchor.ForRoot(rootX, rootZ, Column);
        var rightAnchor = LodTreeAnchor.ForRoot(rootX, rootZ, Column);
        Assert.Equal(8, leftAnchor); // floor((supplied 20 - maximum bias 6) / 8) * 8
        Assert.Equal(leftAnchor, rightAnchor);
        Assert.InRange(sourceReads, 2, 18);

        var trunk = new LodTreeProxy(rootX, rootZ, leftAnchor, 55, 65,
            rootX - 4, rootX + 5, 4, 13, 4, 11, 12,
            VoxelEngine.WorldGen.Trees.Shape.LodCrownProfile.Dome).Trunk();
        var left = trunk.ClipXZ(suppliedX - 64, 0, suppliedX, 64);
        var right = trunk.ClipXZ(suppliedX, 0, suppliedX + 64, 64);
        Assert.True(left.HasVolume && right.HasVolume);
        Assert.Equal(left.MinY, right.MinY);
        Assert.Equal(left.MaxX, right.MinX);
    }

    [Theory]
    [InlineData(63, 64)]
    [InlineData(-1, 0)]
    public void EditedSuppliedNeighborhoodKeepsOneAnchorAcrossLevelsAndRegions(int rootX, int suppliedX)
    {
        const int rootZ = 8;
        LodTreeAnchor.HeightColumn Column(int cx, int cz)
        {
            var heights = new int[Chunk.Size * Chunk.Size];
            Array.Fill(heights, 30);
            if (cx != suppliedX >> Chunk.SizeShift || cz != rootZ >> Chunk.SizeShift)
                return new LodTreeAnchor.HeightColumn(heights, null, null);
            var index = ((suppliedX & Chunk.SizeMask) << Chunk.SizeShift) | (rootZ & Chunk.SizeMask);
            heights[index] = 20;
            var overrides = new long[heights.Length];
            var caps = new int[heights.Length];
            Array.Fill(overrides, LodSurface.None);
            Array.Fill(caps, LodColumnCaps.NoCap);
            overrides[index] = LodSurface.Pack(32, 0);
            caps[index] = 22;
            return new LodTreeAnchor.HeightColumn(heights, overrides, caps);
        }

        var onLeft = LodTreeAnchor.ForRoot(rootX, rootZ, Column);
        var onRight = LodTreeAnchor.ForRoot(rootX, rootZ, Column);
        Assert.Equal(16, onLeft); // effective 22, then maximum bias 6 and 8-block floor
        Assert.Equal(onLeft, onRight);
    }

    [Fact]
    public void ShellOverrideReplacesHeightBeforeApplicableCapLowersIt()
    {
        var heights = new int[Chunk.Size * Chunk.Size];
        var overrides = new long[heights.Length];
        var caps = new int[heights.Length];
        Array.Fill(heights, 30);
        Array.Fill(overrides, LodSurface.None);
        Array.Fill(caps, LodColumnCaps.NoCap);
        overrides[0] = LodSurface.Pack(40, 0);
        caps[0] = 35;
        overrides[1] = LodSurface.Pack(20, 0);
        caps[1] = 25;
        overrides[2] = LodSurface.Pack(40, 0);
        caps[2] = -40; // too deep to apply to the shell
        var column = new LodTreeAnchor.HeightColumn(heights, overrides, caps);

        Assert.Equal(35, LodTreeAnchor.EffectiveHeight(column, 0));
        Assert.Equal(20, LodTreeAnchor.EffectiveHeight(column, 1));
        Assert.Equal(40, LodTreeAnchor.EffectiveHeight(column, 2));
        Assert.Equal(30, LodTreeAnchor.EffectiveHeight(column, 3));
    }

    [Fact]
    public void OrdinaryHeightEditDirtiesAllRootAnchorAndCrownDependentRegions()
    {
        var spec = LodRegion.Levels[0];
        var treeReach = spec.RegionBlocks / 2 - 2;
        var anchorRadius = LodTreeAnchor.RadiusFor(LodRegion.Levels);
        var reach = LodTreeAnchor.EditReach(spec, treeReach, disturbed: false);
        var positiveEdit = spec.RegionBlocks - 1 + treeReach + anchorRadius;
        var negativeEdit = -treeReach - anchorRadius;
        var positive = LodTreeAnchor.RegionRange(positiveEdit, spec, reach);
        var negative = LodTreeAnchor.RegionRange(negativeEdit, spec, reach);

        Assert.True(positive.Min <= 0 && positive.Max >= 0);
        Assert.True(negative.Min <= -1 && negative.Max >= -1);
        Assert.True(reach > spec.SampleMargin + spec.CellSize);
        Assert.True(LodTreeAnchor.EditReach(spec, treeReach, disturbed: true) >= 2 * treeReach);
    }
}
