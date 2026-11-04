using System.Collections.Generic;
using System.Linq;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.WorldGen;

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
        TreeCanopy.ResetCanopyDisturbed();
        TreeGenerator.PoisonCanopy(X, Z, radius);
        Assert.Equal(0, TreeGenerator.CanopyTop(X, Z));
        return TreeCanopy.ExportCanopyDisturbed().ToHashSet();
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
            TreeCanopy.ResetCanopyDisturbed();
        }
    }

    [Fact]
    public void MarkersAreNeverLostHoweverManyColumnsAreEdited()
    {
        try
        {
            TreeCanopy.ResetCanopyDisturbed();
            // Far more fells than the old 65536-marker cap would have kept
            const int fells = 120;
            for (var i = 0; i < fells; i++)
                TreeGenerator.PoisonCanopy(X + i * 64, Z, TreeGenerator.FellPoisonRadius);

            var marked = TreeCanopy.ExportCanopyDisturbed().ToHashSet();
            for (var i = 0; i < fells; i++)
                Assert.Contains((X + i * 64, Z), marked);
            Assert.Equal(fells * (2 * TreeGenerator.FellPoisonRadius + 1) * (2 * TreeGenerator.FellPoisonRadius + 1),
                marked.Count);

            // Importing a saved set unions with what is there and round-trips through the export
            var saved = marked.ToArray();
            TreeCanopy.ResetCanopyDisturbed();
            TreeCanopy.ImportCanopyDisturbed(saved);
            Assert.True(TreeCanopy.ExportCanopyDisturbed().ToHashSet().SetEquals(marked));
        }
        finally
        {
            TreeCanopy.ResetCanopyDisturbed();
        }
    }
}
