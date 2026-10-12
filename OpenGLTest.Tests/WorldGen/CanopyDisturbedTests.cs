using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Rules of <see cref="TreeGenerator.PoisonCanopy"/>'s radius: a single edit disturbs a smaller
/// square than a fell, both cover the edited column, and the small square sits inside the big one.
/// In the LOD column caps collection, beside the other tests that read or write the process-global
/// felled-canopy markers.
/// </summary>
[Collection("LOD column caps")]
public sealed class CanopyDisturbedTests
{
    // Far from every other test's columns, so the markers never land on a canopy someone reads
    private const int X = 912_345;
    private const int Z = -876_543;

    public CanopyDisturbedTests() => TestContent.EnsureLoaded();

    private static HashSet<(int X, int Z)> Poisoned(int radius)
    {
        TreeGenerator.ResetCanopyDisturbed();
        TreeGenerator.PoisonCanopy(X, Z, radius);
        Assert.Equal(0, TreeGenerator.CanopyTop(X, Z));
        return TreeGenerator.ExportCanopyDisturbed().ToHashSet();
    }

    [Fact]
    public void ASingleEditDisturbsLessThanAFellAndInsideIt()
    {
        try
        {
            var single = Poisoned(TreeGenerator.SingleEditPoisonRadius);
            var fell = Poisoned(TreeGenerator.FellPoisonRadius);

            Assert.Contains((X, Z), single);
            Assert.Contains((X, Z), fell);
            Assert.True(single.Count < fell.Count, $"a single edit marked {single.Count} columns, a fell {fell.Count}");
            Assert.True(single.IsSubsetOf(fell), "the single-edit square leaves the fell square");
        }
        finally
        {
            TreeGenerator.ResetCanopyDisturbed();
        }
    }
}
