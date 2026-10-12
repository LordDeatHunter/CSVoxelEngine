using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Shape;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Trees;

/// <summary>
/// Rule tests for the trunk-to-crown proportions: the leaf cluster radius follows the root's
/// support (<see cref="LeafCluster.ClusterRadius(TreeSpecies, TreeGraph)"/>), so a young tree on a
/// thin stem wears a small crown and only a mature trunk carries the full one. Every leafy
/// registered species is checked on flat open ground. Nothing pins a radius, a pixel count, a
/// leaf count or a step number; when one of these fails, the species' wood or leaf data is what
/// needs retuning, not the test.
/// </summary>
public sealed class TreeCrownSupportTests
{
    private static readonly (int X, int Y, int Z, int Seed)[] Samples =
    [
        (0, 1, 0, 11), (37, 1, -12, 222), (-200, 1, 91, 3333), (5, 1, 5, 44444),
        (640, 1, -310, 5), (-77, 1, -77, 66), (1024, 1, 2048, 777), (-512, 1, 300, 8888),
    ];

    public TreeCrownSupportTests() => TestContent.EnsureLoaded();

    /// <summary>A flat world whose ground top is y = 0, so a root stands at y = 1.</summary>
    private static VirtualTreeSpace Flat() => new((_, _) => 0);

    private static IEnumerable<TreeSpecies> LeafySpecies =>
        TreeSpeciesRegistry.All.Where(s => s.HasLeaves).OrderBy(s => s.Name, StringComparer.Ordinal);

    /// <summary>A feature-free grow of <paramref name="steps"/> pulses; without features the
    /// nodes of a shorter grow are a prefix of a longer one's, so steps compare like with like.</summary>
    private static TreeGraph GrowBare(TreeSpecies species, (int X, int Y, int Z, int Seed) at, int steps) =>
        TreeGrower.GrowSteps(species, at.X, at.Y, at.Z, Flat(), at.Seed, steps, applyFeatures: false);

    private static int LeafCount(TreeGraph graph, TreeSpecies species)
    {
        var cells = TreeRasterizer.Rasterize(graph, species, Flat(), out var woodCount);
        return cells.Count - woodCount;
    }

    private static int MaturityAt(TreeSpecies species, (int X, int Y, int Z, int Seed) at) =>
        TreeGrower.MaturityStepsAt(species, at.X, at.Y, at.Z, at.Seed);

    [Fact]
    public void EveryMatureTreeCarriesItsFullCrown()
    {
        foreach (var species in LeafySpecies)
        foreach (var at in Samples)
        {
            var space = Flat();
            var graph = TreeGrower.GrowToMaturity(species, at.X, at.Y, at.Z, space, at.Seed);
            var where = $"{species.Name} at ({at.X}, {at.Z}) seed {at.Seed}, mature";

            Assert.True(LeafCluster.ClusterRadius(species, graph) == species.LeafRadius,
                $"{where}: support {LeafCluster.SupportPx(graph)} px wears a radius " +
                $"{LeafCluster.ClusterRadius(species, graph)} crown, below the full {species.LeafRadius}");

            var tips = Enumerable.Range(0, graph.Count).Count(i => LeafCluster.IsLiveTip(graph, species, i));
            Assert.True(tips > 0, $"{where}: no live tip");

            var cells = TreeRasterizer.Rasterize(graph, species, space, out var woodCount);
            Assert.True(cells.Count > woodCount, $"{where}: the rasterized tree has no leaves");
        }
    }

    [Fact]
    public void ACrownNeverShrinksAsTheTreeGrows()
    {
        foreach (var species in LeafySpecies)
        foreach (var at in Samples)
        {
            var mature = MaturityAt(species, at);
            int lastSupport = 0, lastRadius = 0;
            for (var step = 0; step <= mature; step++)
            {
                var graph = GrowBare(species, at, step);
                var support = LeafCluster.SupportPx(graph);
                var radius = LeafCluster.ClusterRadius(species, graph);
                var where = $"{species.Name} at ({at.X}, {at.Z}) seed {at.Seed}, step {step}";
                Assert.True(support >= lastSupport, $"{where}: support fell from {lastSupport} to {support} px");
                Assert.True(radius >= lastRadius, $"{where}: crown radius fell from {lastRadius} to {radius}");
                lastSupport = support;
                lastRadius = radius;
            }
        }
    }

    [Fact]
    public void AYoungTreeCarriesNoMoreLeavesPerTrunkPixelThanAMatureOne()
    {
        foreach (var species in LeafySpecies)
        foreach (var at in Samples)
        {
            var mature = MaturityAt(species, at);
            var grown = GrowBare(species, at, mature);
            var matureRatio = (double)LeafCount(grown, species) / LeafCluster.SupportPx(grown);

            foreach (var step in new[] { mature / 4, mature / 2 })
            {
                var young = GrowBare(species, at, step);
                var leaves = LeafCount(young, species);
                var support = LeafCluster.SupportPx(young);
                Assert.True((double)leaves / support <= matureRatio,
                    $"{species.Name} at ({at.X}, {at.Z}) seed {at.Seed}, step {step} of {mature}: " +
                    $"{leaves} leaves on a {support} px trunk, more per pixel than the mature " +
                    $"{matureRatio:F2}");
            }
        }
    }

    /// <summary>
    /// Species whose crown scales and whose trunk stays a single column. A species that can grow
    /// wide-trunk shells (<see cref="TreeRasterizer.BlockRadius"/> of its cap above 0) is left out:
    /// the support is the root cell's stored diameter, capped at one block, and such a trunk
    /// passes one block within its first pulses, so its crown only scales as a seedling.
    /// </summary>
    [Fact]
    public void AYoungTreeWearsASmallerCrownThanAtMaturity()
    {
        foreach (var species in LeafySpecies.Where(s => s.LeafMinRadius < s.LeafRadius
                                                        && TreeRasterizer.BlockRadius(s.MaxPx) == 0))
        foreach (var at in Samples)
        {
            var mature = MaturityAt(species, at);
            var young = LeafCluster.ClusterRadius(species, GrowBare(species, at, mature / 4));
            var full = LeafCluster.ClusterRadius(species, GrowBare(species, at, mature));
            Assert.True(young < full,
                $"{species.Name} at ({at.X}, {at.Z}) seed {at.Seed}: a quarter-grown tree " +
                $"(step {mature / 4} of {mature}) already wears radius {young}, mature {full}");
        }
    }
}
