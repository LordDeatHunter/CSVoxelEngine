using System.Collections.Generic;
using VoxelEngine.Tests.Support;
using VoxelEngine.WikiExport;
using VoxelEngine.WorldGen.Trees;
using Xunit;

namespace VoxelEngine.Tests.Wiki;

public class TreeStepDiffTests
{
    private static readonly TreeStepDiff.Cell Wood = new(false, 3);
    private static readonly TreeStepDiff.Cell Leaf = new(true, 5);

    [Fact]
    public void TheDiffOfTwoEqualMapsIsEmpty()
    {
        var map = new Dictionary<(int X, int Y, int Z), TreeStepDiff.Cell> { [(0, 1, 0)] = Wood, [(0, 2, 0)] = Leaf };
        var step = TreeStepDiff.Between(map, new Dictionary<(int X, int Y, int Z), TreeStepDiff.Cell>(map));
        Assert.Empty(step.Set);
        Assert.Empty(step.Del);
    }

    [Fact]
    public void AChangedStateIsWrittenAndAVanishedCellIsRemoved()
    {
        var before = new Dictionary<(int X, int Y, int Z), TreeStepDiff.Cell> { [(0, 1, 0)] = Wood, [(1, 1, 0)] = Leaf };
        var after = new Dictionary<(int X, int Y, int Z), TreeStepDiff.Cell> { [(0, 1, 0)] = new(false, 9) };

        var step = TreeStepDiff.Between(before, after);

        Assert.Equal([0, 1, 0, 0, 9], step.Set);
        Assert.Equal([1, 1, 0], step.Del);
    }

    [Fact]
    public void ReplayingEveryStepReproducesTheGrownTreeAtThatStepForEverySpecies()
    {
        TestContent.EnsureLoaded();
        foreach (var species in TreeSpeciesRegistry.All)
        {
            var steps = TreeExporter.GrowSteps(species, WorldSeed.Default, out var maturity);
            Assert.Equal(species.MaturityStepsMax + 1, steps.Count);

            var replayed = new Dictionary<(int X, int Y, int Z), TreeStepDiff.Cell>();
            for (var n = 0; n < steps.Count; n++)
            {
                TreeStepDiff.Apply(replayed, steps[n]);
                var grown = TreeExporter.CellsAt(species, WorldSeed.Default, n, maturity);
                Assert.True(grown.Count == replayed.Count && !MissingOrDifferent(grown, replayed),
                    $"{species.Name} diverges from its grown tree at step {n}");
            }
        }
    }

    private static bool MissingOrDifferent(
        Dictionary<(int X, int Y, int Z), TreeStepDiff.Cell> expected,
        Dictionary<(int X, int Y, int Z), TreeStepDiff.Cell> actual)
    {
        foreach (var (p, cell) in expected)
            if (!actual.TryGetValue(p, out var other) || other != cell) return true;
        return false;
    }
}
