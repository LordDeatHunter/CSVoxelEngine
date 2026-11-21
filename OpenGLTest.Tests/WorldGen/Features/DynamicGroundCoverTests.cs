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
    public void WeightedSelectorReachesEachPositiveMemberAtItsExactCumulativeEdge()
    {
        float[] weights = [2f, 0f, 3f, 1f];
        float[] scaled = [20f, 0f, 30f, 10f];
        var edges = new (float Roll, int Member)[]
        {
            (0f, 0), (MathF.BitDecrement(2f / 6f), 0), (2f / 6f, 2),
            (MathF.BitDecrement(5f / 6f), 2), (5f / 6f, 3), (1f, 3),
        };
        foreach (var (roll, member) in edges)
        {
            Assert.Equal(member, DynamicGroundCover.WeightedPick(roll, weights));
            Assert.Equal(member, DynamicGroundCover.WeightedPick(roll, scaled));
        }
        Assert.Equal(-1, DynamicGroundCover.WeightedPick(0.5f, [0f, 0f, 0f]));
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
