using System;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Features;

/// <summary>Pure cover selection and profile reset contracts; real fill sites are a separate control.</summary>
[Collection("Worldgen seed")]
public sealed class DynamicGroundCoverTests : IDisposable
{
    public DynamicGroundCoverTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
    }

    public void Dispose() => WorldSeed.Apply(WorldSeed.Default);

    [Fact]
    public void WeightedSelectorNeverChoosesZeroWeightAndIgnoresUniformScale()
    {
        float[] weights = [2f, 0f, 3f, 0f];
        float[] scaled = [20f, 0f, 30f, 0f];
        for (var i = 0; i < 100; i++)
        {
            var roll = i / 100f;
            var selected = DynamicGroundCover.WeightedPick(roll, weights);
            Assert.True(selected is 0 or 2);
            Assert.Equal(selected, DynamicGroundCover.WeightedPick(roll, scaled));
        }
    }

    [Fact]
    public void DynamicVariantDecisionRepeatsAfterProfileAndSeedReset()
    {
        var first = DynamicGroundCover.CandidateAt(432, 816, Biome.Jungle);
        WorldSeed.Apply(2112, WorldGenProfile.Legacy);
        Assert.Equal(BlockType.Air, DynamicGroundCover.CandidateAt(432, 816, Biome.Jungle));
        WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
        Assert.Equal(first, DynamicGroundCover.CandidateAt(432, 816, Biome.Jungle));
    }
}
