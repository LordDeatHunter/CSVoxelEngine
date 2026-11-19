using VoxelEngine.WorldGen.Volcanoes;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Volcanoes;

/// <summary>Integer-only deep geometry controls; no seed, registry or column is initialized.</summary>
public sealed class VolcanoDeepShapeTests
{
    private static readonly VolcanoSite Deep = new(true, 0f, 0f, 100f, 20f, 120, 6, 100f,
        new VolcanoDeep(true, 0, 0));
    private static readonly VolcanoSite SurfaceOnly = Deep with { Deep = default };

    private static VolcanoCell At(int x, int y, int z) => VolcanoShape.Classify(Deep, x, y, z, Deep.Floor);

    [Fact]
    public void IntegerAxisPiercesTheOldBedAndJoinsBothSourcePools()
    {
        Assert.Equal(VolcanoCell.Lining, VolcanoShape.Classify(SurfaceOnly, 0, 113, 0, 120));
        Assert.Equal(VolcanoCell.Lava, At(0, 113, 0));
        Assert.Equal(VolcanoCell.Lining, At(10, 113, 0));
        for (var y = WorldGenerationConfig.VolcanoChamberLavaSurfaceY; y <= Deep.Floor; y++)
            Assert.Equal(VolcanoCell.Lava, At(0, y, 0));
        Assert.Equal(VolcanoCell.Lava, At(0, -226, 0));
        Assert.Equal(VolcanoCell.Lining, At(0, VolcanoShape.ChamberBottom, 0));

        var negative = Deep with { X = -0.25f, Z = -0.75f, Deep = new VolcanoDeep(true, -1, -1) };
        Assert.Equal(VolcanoCell.Lava, VolcanoShape.Classify(negative, -1, 113, -1, negative.Floor));
        Assert.Equal(VolcanoCell.Lining, VolcanoShape.Classify(negative, -8, 113, -1, negative.Floor));
    }

    [Fact]
    public void GiantPoolHasAnIndependentInscribedSourceBoxAndSealedSamples()
    {
        var sources = 0;
        for (var x = -22; x <= 22; x++)
        for (var z = -22; z <= 22; z++)
        for (var y = -226; y <= -214; y++)
        {
            Assert.Equal(VolcanoCell.Lava, At(x, y, z));
            sources++;
        }
        Assert.True(sources > WorldGenerationConfig.CaveLavaLakeDeepArea
            * WorldGenerationConfig.CaveLavaLakeMaxDepth);
        Assert.Equal(VolcanoCell.Air, At(47, -208, 0));
        Assert.Equal(VolcanoCell.Lining, At(50, -208, 0));
        Assert.Equal(VolcanoCell.None, At(51, -208, 0));

        // The proof in the owning doc covers all integer points. These independent
        // boundary slices guard the classifier's shell and wake-neighbor priority.
        foreach (var y in new[] { -230, -226, -214, -208, -185, -182 })
        foreach (var (x, z) in new[] { (0, 0), (4, 0), (5, 0), (22, 22), (44, 0), (47, 0), (50, 0) })
        {
            var cell = At(x, y, z);
            if (cell == VolcanoCell.Air)
                foreach (var (dx, dy, dz) in new[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0),
                             (0, -1, 0), (0, 0, 1), (0, 0, -1) })
                    Assert.NotEqual(VolcanoCell.None, At(x + dx, y + dy, z + dz));
            if (cell == VolcanoCell.Lava)
                foreach (var (dx, dy, dz) in new[] { (1, 0, 0), (-1, 0, 0), (0, -1, 0),
                             (0, 0, 1), (0, 0, -1) })
                    Assert.True(At(x + dx, y + dy, z + dz) is VolcanoCell.Lava or VolcanoCell.Lining);
        }
    }

    [Fact]
    public void DeepOwnedBoxesExtendPastSmallCraterGuardWithoutChangingSurfaceOnlySites()
    {
        Assert.True(VolcanoShape.InDeepBox(Deep, 48, -208, 0, 50, -208, 0));
        Assert.True(VolcanoShape.InDeepBox(Deep, -6, 0, -6, -6, 0, -6));
        Assert.False(VolcanoShape.InDeepBox(Deep, 55, -208, 0, 56, -208, 0));
        Assert.False(VolcanoShape.InDeepBox(Deep, 48, 0, 0, 50, 0, 0));
        Assert.False(VolcanoShape.InDeepBox(SurfaceOnly, -50, -208, -50, 50, -208, 50));
        Assert.Equal(VolcanoCell.None, VolcanoShape.Classify(SurfaceOnly, 0, -208, 0, 120));
    }
}
