using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Shape;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Trees;

/// <summary>
/// Rule tests only for the crown-scaling half of <see cref="LeafCluster"/>: the cluster radius
/// follows the tree's support monotonically within its range, every leaf shape nests in its
/// larger radii, and a scaled cluster never reaches past (or hydrates beyond) the full-radius one
/// the decay manager precomputes. No radius, pixel count or shape size is pinned.
/// </summary>
public sealed class LeafClusterScalingTests
{
    private const int MaxTestRadius = 8;

    public LeafClusterScalingTests() => TestContent.EnsureLoaded();

    /// <summary>Every leafy registered species, plus a copy of each that does scale from radius 1
    /// so the rule is exercised even while shipped data leaves the knobs at their defaults.</summary>
    private static IEnumerable<TreeSpecies> LeafySpeciesAndScaledClones()
    {
        foreach (var s in TreeSpeciesRegistry.All.Where(s => s.HasLeaves))
        {
            yield return s;
            var full = Math.Min(s.MaxPx, WoodState.PixelsPerBlock);
            yield return WideTreeSpecies.WithLeafScaling(s, 1, Math.Max(1, full / 2));
        }
    }

    [Fact]
    public void ClusterRadiusGrowsWithTheTrunkAndStaysInItsRange()
    {
        foreach (var s in LeafySpeciesAndScaledClones())
        {
            var previous = int.MinValue;
            for (var support = 1; support <= WoodState.PixelsPerBlock; support++)
            {
                var r = LeafCluster.ClusterRadius(s, support);
                Assert.True(r >= previous, $"{s.Name}: radius fell from {previous} to {r} at support {support}");
                Assert.InRange(r, s.LeafMinRadius, s.LeafRadius);
                if (support >= s.LeafFullRadiusPx)
                    Assert.True(r == s.LeafRadius, $"{s.Name}: support {support} reaches the full radius but got {r}");
                previous = r;
            }
        }
    }

    [Fact]
    public void EveryShapeNestsInItsLargerRadius()
    {
        foreach (var shape in Enum.GetValues<LeafClusterShape>())
        for (var r = 1; r <= MaxTestRadius; r++)
        for (var rs = 1; rs <= r; rs++)
        for (var dx = -r; dx <= r; dx++)
        for (var dy = -r; dy <= r; dy++)
        for (var dz = -r; dz <= r; dz++)
        {
            if (LeafCluster.InShape(shape, rs, dx, dy, dz))
                Assert.True(LeafCluster.InShape(shape, r, dx, dy, dz),
                    $"{shape}: offset ({dx},{dy},{dz}) is inside radius {rs} but outside radius {r}");
        }
    }

    [Fact]
    public void TheFullRadiusOverloadIsTheLargestCluster()
    {
        foreach (var s in LeafySpeciesAndScaledClones())
        {
            // A margin past the full radius catches a scaled cluster leaking outside it
            var reach = s.LeafRadius + 1;
            for (var support = 0; support <= WoodState.PixelsPerBlock; support++)
            {
                var radius = LeafCluster.ClusterRadius(s, support);
                for (var dx = -reach; dx <= reach; dx++)
                for (var dy = -reach; dy <= reach; dy++)
                for (var dz = -reach; dz <= reach; dz++)
                {
                    var scaled = LeafCluster.HydrationFromTip(s, radius, dx, dy, dz);
                    var full = LeafCluster.HydrationFromTip(s, dx, dy, dz);
                    Assert.True(scaled <= full,
                        $"{s.Name}: support {support} (radius {radius}) gives ({dx},{dy},{dz}) hydration {scaled} over the full cluster's {full}");
                }
            }
        }
    }

    /// <summary>A pad is a cap resting on its tip: no radius ever puts a leaf below the tip's
    /// level, which is what leaves the limb under it bare.</summary>
    [Fact]
    public void APadNeverReachesBelowItsTip()
    {
        for (var r = 1; r <= MaxTestRadius; r++)
        for (var dx = -r - 1; dx <= r + 1; dx++)
        for (var dy = -r - 1; dy < 0; dy++)
        for (var dz = -r - 1; dz <= r + 1; dz++)
            Assert.False(LeafCluster.InShape(LeafClusterShape.Pad, r, dx, dy, dz),
                $"radius {r}: pad holds ({dx},{dy},{dz}) below its tip");
    }

    /// <summary>Even the smallest pad covers the cell above its tip and the four beside it, so a
    /// live tip with open air above or beside it always wears at least one leaf.</summary>
    [Fact]
    public void APadCoversTheCellAboveAndBesideItsTip()
    {
        (int X, int Y, int Z)[] around = [(0, 1, 0), (1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1)];
        for (var r = 1; r <= MaxTestRadius; r++)
            foreach (var (x, y, z) in around)
                Assert.True(LeafCluster.InShape(LeafClusterShape.Pad, r, x, y, z),
                    $"radius {r}: pad misses ({x},{y},{z}) next to its tip");
    }
}
