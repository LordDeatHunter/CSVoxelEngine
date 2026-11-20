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

    [Fact]
    public void RectangleDisturbanceMatchesFineColumnTruthAcrossChunkAndWordSeams()
    {
        var markers = new (int X, int Z)[]
        {
            (-17, -17), (-16, -1), (-13, 0), (-12, 15),
            (-9, 16), (-8, 31), (-5, -16), (-4, 1),
            (-1, 63), (0, -32), (3, 47), (4, 48),
            (7, -2), (8, 2), (15, 3), (16, 16)
        };
        try
        {
            TreeCanopy.ResetCanopyDisturbed();
            Assert.False(TreeCanopy.AnyDisturbed(-32, -32, 32, 32));
            Assert.False(TreeCanopy.AnyDisturbed(0, 0, 0, 1));
            for (var group = 0; group < 4; group++)
            {
                TreeCanopy.ResetCanopyDisturbed();
                TreeCanopy.ImportCanopyDisturbed(markers[(group * 4)..(group * 4 + 4)]);
                for (var n = 0; n < 128; n++)
                {
                    var minX = -40 + (n * 17 + group * 9) % 65;
                    var minZ = -40 + (n * 29 + group * 11) % 97;
                    var maxX = minX + n % 65;
                    var maxZ = minZ + (n * 7) % 65;
                    var expected = false;
                    for (var x = minX; x < maxX && !expected; x++)
                    for (var z = minZ; z < maxZ; z++)
                        if (TreeCanopy.IsDisturbed(x, z)) { expected = true; break; }
                    Assert.Equal(expected, TreeCanopy.AnyDisturbed(minX, minZ, maxX, maxZ));
                }
                foreach (var (x, z) in markers[(group * 4)..(group * 4 + 4)])
                {
                    Assert.True(TreeCanopy.AnyDisturbed(x, z, x + 1, z + 1));
                    Assert.False(TreeCanopy.AnyDisturbed(x, z, x, z + 1));
                    Assert.False(TreeCanopy.AnyDisturbed(x, z, x + 1, z));
                }
            }
        }
        finally
        {
            TreeCanopy.ResetCanopyDisturbed();
        }
    }
}
