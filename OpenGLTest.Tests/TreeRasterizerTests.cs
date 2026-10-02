using System;
using System.Collections.Generic;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Rules of <see cref="TreeRasterizer"/> and <see cref="LeafCluster"/>: output order, the leaf
/// hydration rule, obstacles, and parent codes that decode onto wood. Every shipped species is
/// checked through <see cref="TreeSpeciesRegistry.All"/>; nothing pins a count or a JSON number.
/// </summary>
public sealed class TreeRasterizerTests
{
    private const int Seed = 1234;

    private static readonly (int X, int Y, int Z)[] Positions =
        [(0, 1, 0), (37, 1, -12), (-200, 1, 91), (5, 1, 5)];

    public TreeRasterizerTests() => TestContent.EnsureLoaded();

    /// <summary>A flat world whose ground top is y = 0, so a root stands at y = 1.</summary>
    private static VirtualTreeSpace Flat() => new((_, _) => 0);

    private static TreeGraph Grow(TreeSpecies species, (int X, int Y, int Z) pos, ITreeSpace space) =>
        TreeGrower.GrowToMaturity(species, pos.X, pos.Y, pos.Z, space, Seed);

    private static long Key(int x, int y, int z) => TreeCellKey.Pack(x, y, z);

    [Fact]
    public void WoodComesBeforeFeaturesBeforeLeaves()
    {
        foreach (var species in TreeSpeciesRegistry.All)
        foreach (var pos in Positions)
        {
            // Carve a column under the base so species with roots really grow feature nodes.
            var space = new VirtualTreeSpace((_, _) => 0, (x, y, z) => x == pos.X && z == pos.Z && y < pos.Y);
            var graph = Grow(species, pos, space);
            var cells = TreeRasterizer.Rasterize(graph, species, space);

            var lastWood = -1;
            var firstLeaf = int.MaxValue;
            var lastGrowthWood = -1;
            var firstFeatureWood = int.MaxValue;
            for (var i = 0; i < cells.Count; i++)
            {
                var c = cells[i];
                if (c.Block == species.WoodId)
                {
                    lastWood = i;
                    var node = graph.NodeAt(c.X, c.Y, c.Z);
                    Assert.True(node >= 0);
                    if (graph[node].IsFeature)
                        firstFeatureWood = Math.Min(firstFeatureWood, i);
                    else
                        lastGrowthWood = i;
                }
                else
                {
                    Assert.True(species.HasLeaves);
                    Assert.Equal(species.LeavesId, c.Block);
                    firstLeaf = Math.Min(firstLeaf, i);
                }
            }
            Assert.True(lastWood < firstLeaf);
            Assert.True(lastGrowthWood < firstFeatureWood);
        }
    }

    [Fact]
    public void EveryLeafIsWithinHydrationOfALiveTip()
    {
        foreach (var species in TreeSpeciesRegistry.All)
        foreach (var pos in Positions)
        {
            var space = Flat();
            var graph = Grow(species, pos, space);
            var cells = TreeRasterizer.Rasterize(graph, species, space);

            foreach (var c in cells)
            {
                if (c.Block == species.WoodId)
                    continue;
                Assert.True(species.HasLeaves, $"{species.Name} is leafless but emitted {c}");

                var h = LeafState.Hydration(c.State);
                Assert.InRange(h, 1, LeafState.MaxHydration);

                var expected = int.MinValue;
                var nearest = int.MaxValue;
                for (var t = 0; t < graph.Count; t++)
                {
                    if (!LeafCluster.IsLiveTip(graph, species, t))
                        continue;
                    var tip = graph[t];
                    int dx = c.X - tip.X, dy = c.Y - tip.Y, dz = c.Z - tip.Z;
                    var distance = LeafCluster.Distance(dx, dy, dz);
                    nearest = Math.Min(nearest, distance);
                    if (LeafCluster.InShape(species.LeafShape, species.LeafRadius, dx, dy, dz))
                        expected = Math.Max(expected, species.LeafHydration - distance);
                }

                Assert.Equal(expected, h);
                Assert.True(expected >= 1);
                Assert.Equal(h, LeafCluster.HydrationAt(graph, species, c.X, c.Y, c.Z));
                Assert.True(nearest <= species.LeafHydration - h);
            }
        }
    }

    [Fact]
    public void LeavesNeverOverwriteWoodTerrainOrOccupiedCells()
    {
        foreach (var species in TreeSpeciesRegistry.All)
        foreach (var pos in Positions)
        {
            var rootX = pos.X;
            int Height(int x, int z) => x >= rootX + 2 ? 20 : 0;
            var space = new VirtualTreeSpace(Height);
            for (var y = 1; y <= 16; y++)
            {
                space.Occupy(pos.X - 1, y, pos.Z);
                space.Occupy(pos.X, y, pos.Z - 2);
            }

            var cells = TreeRasterizer.Rasterize(Grow(species, pos, space), species, space);
            var wood = new HashSet<long>();
            foreach (var c in cells)
            {
                if (c.Block == species.WoodId)
                    wood.Add(Key(c.X, c.Y, c.Z));
            }

            var leaves = new HashSet<long>();
            foreach (var c in cells)
            {
                if (c.Block == species.WoodId)
                    continue;
                var key = Key(c.X, c.Y, c.Z);
                Assert.True(leaves.Add(key), $"{species.Name} emitted the leaf {c} twice");
                Assert.DoesNotContain(key, wood);
                Assert.False(space.IsOccupied(c.X, c.Y, c.Z), $"{species.Name} put a leaf in an occupied cell at {c}");
                Assert.True(c.Y > Height(c.X, c.Z), $"{species.Name} put a leaf in terrain at {c}");
            }
        }
    }

    [Fact]
    public void EveryParentCodeDecodesOntoAWoodCell()
    {
        foreach (var species in TreeSpeciesRegistry.All)
        foreach (var pos in Positions)
        {
            var space = new VirtualTreeSpace((_, _) => 0, (x, y, z) => x == pos.X && z == pos.Z && y < pos.Y);
            var cells = TreeRasterizer.Rasterize(Grow(species, pos, space), species, space);

            var wood = new HashSet<long>();
            foreach (var c in cells)
            {
                if (c.Block == species.WoodId)
                    wood.Add(Key(c.X, c.Y, c.Z));
            }

            var roots = 0;
            foreach (var c in cells)
            {
                if (c.Block != species.WoodId)
                    continue;
                Assert.False(WoodState.IsDetached(c.State));
                Assert.False(WoodState.IsShell(c.State));
                if (WoodState.IsRoot(c.State))
                    roots++;
                if (WoodState.TryParentOffset(c.State, out var dx, out var dy, out var dz))
                    Assert.Contains(Key(c.X + dx, c.Y + dy, c.Z + dz), wood);
            }
            Assert.Equal(1, roots);
        }
    }

    [Fact]
    public void EveryLiveTipInOpenAirWearsLeaves()
    {
        foreach (var species in TreeSpeciesRegistry.All)
        {
            if (!species.HasLeaves || species.LeafHydration < 2)
                continue;

            foreach (var pos in Positions)
            {
                var space = Flat();
                var graph = Grow(species, pos, space);
                var leaves = new HashSet<long>();
                foreach (var c in TreeRasterizer.Rasterize(graph, species, space))
                {
                    if (c.Block == species.LeavesId)
                        leaves.Add(Key(c.X, c.Y, c.Z));
                }

                for (var t = 0; t < graph.Count; t++)
                {
                    if (!LeafCluster.IsLiveTip(graph, species, t))
                        continue;
                    var tip = graph[t];
                    var free = 0;
                    var leafy = 0;
                    for (var d = 0; d < TreeDirections.Count; d++)
                    {
                        int x = tip.X + TreeDirections.Dx[d], y = tip.Y + TreeDirections.Dy[d], z = tip.Z + TreeDirections.Dz[d];
                        if (graph.Occupies(x, y, z) || !space.IsFree(x, y, z))
                            continue;
                        free++;
                        if (leaves.Contains(Key(x, y, z)))
                            leafy++;
                    }
                    if (free > 0)
                        Assert.True(leafy > 0, $"{species.Name}: live tip {t} at ({tip.X}, {tip.Y}, {tip.Z}) has no leaves");
                }
            }
        }
    }
}
