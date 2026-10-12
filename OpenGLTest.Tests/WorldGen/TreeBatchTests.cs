using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Shape;
using Xunit;

namespace VoxelEngine.Tests.WorldGen;

/// <summary>
/// Rules of the per-cell tree batches (<see cref="TreeBatch"/>, <see cref="TreeGenerator.BatchOf"/>):
/// a batch is a pure function of its cell whatever order cells are asked for in, its trunks are
/// exactly the cell's placement in placement-hash order, no grown cell of one tree enters a cell
/// already held by an earlier tree of the batch or a neighbor cell's solo shape, and every tree
/// stays inside its species' reach, a tree crossing a cell border is stamped whole, and the
/// first LOD ring's canopy is the canopy the chunks stamp. In the shape-cache collection because
/// one fact resets the tree caches.
/// </summary>
[Collection("Worldgen shape cache")]
public sealed class TreeBatchTests
{
    public TreeBatchTests() => TestContent.EnsureLoaded();

    private static int CellTrunks(int cellX, int cellZ)
    {
        var x0 = cellX << WorldGenerationConfig.TreeCellShift;
        var z0 = cellZ << WorldGenerationConfig.TreeCellShift;
        var count = 0;
        for (var x = x0; x < x0 + WorldGenerationConfig.TreeCellBlocks; x++)
        for (var z = z0; z < z0 + WorldGenerationConfig.TreeCellBlocks; z++)
            if (TreeGenerator.TryGetTree(x, z, out _, out _))
                count++;
        return count;
    }

    /// <summary>A forest cell rooting at least <paramref name="minTrunks"/> trees, searched outward from the origin.</summary>
    private static (int CellX, int CellZ) FindForestCellWithTrunks(int minTrunks = 2)
    {
        const int maxRadius = 24;
        var half = WorldGenerationConfig.TreeCellBlocks / 2;
        for (var r = 0; r <= maxRadius; r++)
        for (var cx = -r; cx <= r; cx++)
        for (var cz = -r; cz <= r; cz++)
        {
            if (Math.Max(Math.Abs(cx), Math.Abs(cz)) != r) continue;
            var centerX = (cx << WorldGenerationConfig.TreeCellShift) + half;
            var centerZ = (cz << WorldGenerationConfig.TreeCellShift) + half;
            if (!TerrainGenerator.IsForest(centerX, centerZ)) continue;
            if (CellTrunks(cx, cz) >= minTrunks)
                return (cx, cz);
        }

        throw new InvalidOperationException($"no forest cell with {minTrunks} trunks within {maxRadius} cells");
    }

    /// <summary>The forest cell and its +X neighbor, the cells every fact samples.</summary>
    private static (int CellX, int CellZ)[] SampleCells()
    {
        var (cx, cz) = FindForestCellWithTrunks();
        return [(cx, cz), (cx + 1, cz)];
    }

    /// <summary>
    /// Every solo-shape cell of a trunk in the 2R ring outside the cell. Unfiltered: a trunk the
    /// batch's Chebyshev filter drops cannot reach the cell's trees by the reach bound, so
    /// asserting against the whole ring is the stronger rule.
    /// </summary>
    private static HashSet<long> NeighborSoloCells(int cellX, int cellZ)
    {
        var size = WorldGenerationConfig.TreeCellBlocks;
        var x0 = cellX << WorldGenerationConfig.TreeCellShift;
        var z0 = cellZ << WorldGenerationConfig.TreeCellShift;
        var r = TreeGenerator.ReachHorizontal;
        var cells = new HashSet<long>();
        for (var x = x0 - 2 * r; x < x0 + size + 2 * r; x++)
        for (var z = z0 - 2 * r; z < z0 + size + 2 * r; z++)
        {
            if (x >= x0 && x < x0 + size && z >= z0 && z < z0 + size) continue;
            if (!TreeGenerator.TryGetTree(x, z, out _, out _)) continue;
            foreach (var cell in TreeGenerator.ShapeOf(x, z).Cells)
                cells.Add(Key(cell));
        }

        return cells;
    }

    private static long Key(in TreeCell cell) => TreeCellKey.Pack(cell.X, cell.Y, cell.Z);

    private static List<(int X, int Z, int BaseY, string Species, TreeCell[] Cells)> Snapshot(TreeBatch batch) =>
        batch.Trees.Select(t => (t.X, t.Z, t.BaseY, t.Species.Name, t.Cells)).ToList();

    private static void AssertSameBatch(
        List<(int X, int Z, int BaseY, string Species, TreeCell[] Cells)> expected,
        List<(int X, int Z, int BaseY, string Species, TreeCell[] Cells)> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal((expected[i].X, expected[i].Z, expected[i].BaseY, expected[i].Species),
                (actual[i].X, actual[i].Z, actual[i].BaseY, actual[i].Species));
            Assert.Equal(expected[i].Cells, actual[i].Cells);
        }
    }

    [Fact]
    public void BatchIsIndependentOfTheOrderCellsAreRequested()
    {
        var (ax, az) = FindForestCellWithTrunks();
        var (bx, bz) = (ax + 1, az);

        TreeGenerator.ResetCaches();
        var a1 = Snapshot(TreeGenerator.BatchOf(ax, az));
        var b1 = Snapshot(TreeGenerator.BatchOf(bx, bz));

        TreeGenerator.ResetCaches();
        var b2 = Snapshot(TreeGenerator.BatchOf(bx, bz));
        var a2 = Snapshot(TreeGenerator.BatchOf(ax, az));

        Assert.NotEmpty(a1);
        AssertSameBatch(a1, a2);
        AssertSameBatch(b1, b2);
        Assert.Same(TreeGenerator.BatchOf(ax, az), TreeGenerator.BatchOf(ax, az));
    }

    [Fact]
    public void GrownCellsOfOneBatchNeverShareACell()
    {
        foreach (var (cx, cz) in SampleCells())
        {
            var batch = TreeGenerator.BatchOf(cx, cz);
            var grown = new HashSet<long>();
            var bases = new HashSet<long>();
            foreach (var tree in batch.Trees)
            {
                Assert.True(bases.Add(Key(tree.Cells[0])), $"two trees share the base cell of ({tree.X}, {tree.Z})");

                // The base cell (index 0) is excluded: the grower takes it as given and never
                // checks it, so it may sit in an earlier tree's crown. Every cell it grows is checked.
                for (var i = 1; i < tree.Cells.Length; i++)
                {
                    var c = tree.Cells[i];
                    Assert.True(grown.Add(Key(c)),
                        $"cell ({c.X}, {c.Y}, {c.Z}) of the tree at ({tree.X}, {tree.Z}) is grown twice in cell ({cx}, {cz})");
                }
            }
        }
    }

    [Fact]
    public void AGrownCellNeverEntersEarlierTreesOrNeighborSoloCells()
    {
        foreach (var (cx, cz) in SampleCells())
        {
            var batch = TreeGenerator.BatchOf(cx, cz);
            var occupied = NeighborSoloCells(cx, cz);
            foreach (var tree in batch.Trees)
            {
                for (var i = 1; i < tree.Cells.Length; i++)
                {
                    var c = tree.Cells[i];
                    Assert.False(occupied.Contains(Key(c)),
                        $"cell ({c.X}, {c.Y}, {c.Z}) of the tree at ({tree.X}, {tree.Z}) enters an occupied cell");
                }

                // Every cell of this tree, base included, is an obstacle for the trees after it
                foreach (var c in tree.Cells)
                    occupied.Add(Key(c));
            }
        }
    }

    [Fact]
    public void BatchTrunksAreExactlyTheCellsPlacement()
    {
        foreach (var (cx, cz) in SampleCells())
        {
            var x0 = cx << WorldGenerationConfig.TreeCellShift;
            var z0 = cz << WorldGenerationConfig.TreeCellShift;
            var placed = new HashSet<(int X, int Z, TreeSpecies Species, int BaseY)>();
            for (var x = x0; x < x0 + WorldGenerationConfig.TreeCellBlocks; x++)
            for (var z = z0; z < z0 + WorldGenerationConfig.TreeCellBlocks; z++)
                if (TreeGenerator.TryGetTree(x, z, out var species, out var baseY))
                    placed.Add((x, z, species, baseY));

            var batch = TreeGenerator.BatchOf(cx, cz);
            var trunks = batch.Trees.Select(t => (t.X, t.Z, t.Species, t.BaseY)).ToHashSet();
            Assert.Equal(batch.Trees.Length, trunks.Count);
            Assert.True(placed.SetEquals(trunks), $"cell ({cx}, {cz}) batch trunks differ from its placement");

            for (var i = 1; i < batch.Trees.Length; i++)
            {
                var a = batch.Trees[i - 1];
                var b = batch.Trees[i];
                var order = (TreeGenerator.PlacementHash(a.X, a.Z), a.X, a.Z)
                    .CompareTo((TreeGenerator.PlacementHash(b.X, b.Z), b.X, b.Z));
                Assert.True(order < 0, $"({a.X}, {a.Z}) is not before ({b.X}, {b.Z}) in placement-hash order");
            }
        }
    }

    [Fact]
    public void BatchTreesStayWithinTheirReach()
    {
        var trees = 0;
        foreach (var (cx, cz) in SampleCells())
        {
            var batch = TreeGenerator.BatchOf(cx, cz);
            trees += batch.Trees.Length;
            foreach (var tree in batch.Trees)
            {
                var (horizontal, up, down) = TreeRasterizer.ReachOf(tree.Species);
                foreach (var c in tree.Cells)
                {
                    var dy = c.Y - tree.BaseY;
                    Assert.True(Math.Max(Math.Abs(c.X - tree.X), Math.Abs(c.Z - tree.Z)) <= horizontal
                                && dy <= up && -dy <= down,
                        $"cell ({c.X}, {c.Y}, {c.Z}) leaves the reach of the {tree.Species.Name} at ({tree.X}, {tree.BaseY}, {tree.Z})");
                }

                Assert.InRange(tree.MinY, batch.MinY, batch.MaxY);
                Assert.InRange(tree.MaxY, batch.MinY, batch.MaxY);
            }
        }

        Assert.True(trees > 0, "no batch tree was sampled");
    }

    /// <summary>
    /// A forest trunk within <see cref="TreeGenerator.ReachHorizontal"/> of a cell border whose
    /// batch shape crosses into a neighbor cell and whose 3x3 chunk-column neighborhood is forest,
    /// searched in rings of cells outward from the origin, then by column.
    /// </summary>
    private static (int X, int Z) FindForestTrunkNearCellBorder()
    {
        const int maxRadius = 24;
        var size = WorldGenerationConfig.TreeCellBlocks;
        var mask = size - 1;
        var r = TreeGenerator.ReachHorizontal;
        for (var ring = 0; ring <= maxRadius; ring++)
        for (var cx = -ring; cx <= ring; cx++)
        for (var cz = -ring; cz <= ring; cz++)
        {
            if (Math.Max(Math.Abs(cx), Math.Abs(cz)) != ring) continue;
            var x0 = cx << WorldGenerationConfig.TreeCellShift;
            var z0 = cz << WorldGenerationConfig.TreeCellShift;
            for (var x = x0; x < x0 + size; x++)
            for (var z = z0; z < z0 + size; z++)
            {
                var nearBorder = (x & mask) < r || (x & mask) > mask - r || (z & mask) < r || (z & mask) > mask - r;
                if (!nearBorder || !TreeGenerator.TryGetTree(x, z, out _, out _)) continue;

                var chunkX = x >> Chunk.SizeShift;
                var chunkZ = z >> Chunk.SizeShift;
                var allForest = true;
                for (var dx = -1; dx <= 1 && allForest; dx++)
                for (var dz = -1; dz <= 1 && allForest; dz++)
                    allForest = TerrainGenerator.IsForest((chunkX + dx) * Chunk.Size + 8, (chunkZ + dz) * Chunk.Size + 8);
                if (!allForest) continue;

                // Only a tree whose batch shape really crosses into another cell tests the border
                var crosses = TreeGenerator.BatchShapeOf(x, z).Cells.Any(c =>
                    TreeGenerator.CellOf(c.X) != cx || TreeGenerator.CellOf(c.Z) != cz);
                if (crosses)
                    return (x, z);
            }
        }

        throw new InvalidOperationException($"no forest trunk near a cell border within {maxRadius} cells");
    }

    [Fact]
    public void BatchTreesAreStampedWholeAcrossCellBorders()
    {
        var (x, z) = FindForestTrunkNearCellBorder();
        var tree = TreeGenerator.BatchShapeOf(x, z);
        var world = TreeWorldgenFixture.Generate(x >> Chunk.SizeShift, z >> Chunk.SizeShift, reverse: false);
        var woodIds = TreeWorldgenFixture.WoodIds();

        // A tree reaches at most one chunk from its trunk, so the 3x3 columns hold all of it. Wood
        // is first-come at a border, so a cell may hold another tree's wood, but it is always wood;
        // a leaf may lose its cell to wood, never to air.
        for (var i = 0; i < tree.Cells.Length; i++)
        {
            var c = tree.Cells[i];
            Assert.True(world.TryGetValue((c.X, c.Y, c.Z), out var cell),
                $"cell ({c.X}, {c.Y}, {c.Z}) of the tree at ({x}, {z}) lies outside the generated band");
            if (i < tree.WoodCount)
                Assert.True(woodIds.Contains(cell.Block),
                    $"wood cell ({c.X}, {c.Y}, {c.Z}) of the tree at ({x}, {z}) was stamped as block {cell.Block}");
            else
                Assert.True(cell.Block != BlockType.Air,
                    $"leaf cell ({c.X}, {c.Y}, {c.Z}) of the tree at ({x}, {z}) was left air");
        }
    }

    /// <summary>
    /// Wooded forest chunk columns whose 3x3 neighborhood is forest and whose whole 16x16 lies at
    /// least <see cref="TreeGenerator.ReachHorizontal"/> from every cell border, so only their own
    /// cell's trees reach them, searched in rings outward from the origin.
    /// </summary>
    // Wooded forest chunk columns, searched in rings from the origin. No margin from a tree
    // cell's border is needed: a chunk's canopy aggregates every batch that can overhang it
    // (TreeGenerator.GetCanopyColumns), so the comparison holds at any reach.
    private static IEnumerable<(int Cx, int Cz)> WoodedForestColumns()
    {
        const int maxRadius = 96;
        for (var ring = 1; ring <= maxRadius; ring++)
        for (var cx = -ring; cx <= ring; cx++)
        for (var cz = -ring; cz <= ring; cz++)
        {
            if (Math.Max(Math.Abs(cx), Math.Abs(cz)) != ring) continue;

            var allForest = true;
            for (var dx = -1; dx <= 1 && allForest; dx++)
            for (var dz = -1; dz <= 1 && allForest; dz++)
                allForest = TerrainGenerator.IsForest((cx + dx) * Chunk.Size + 8, (cz + dz) * Chunk.Size + 8);
            if (!allForest) continue;

            var wooded = false;
            for (var x = 0; x < Chunk.Size && !wooded; x++)
            for (var z = 0; z < Chunk.Size && !wooded; z++)
                wooded = TreeGenerator.TryGetTree(cx * Chunk.Size + x, cz * Chunk.Size + z, out _, out _);
            if (wooded)
                yield return (cx, cz);
        }
    }

    [Fact]
    public void FirstRingCanopyEqualsTheStampedCanopy()
    {
        var leafIds = TreeWorldgenFixture.LeafIds();
        var canopied = 0;
        var tried = 0;
        foreach (var (cx, cz) in WoodedForestColumns())
        {
            // Widen the search until some column actually carries a canopy, a few columns at most
            if (canopied > 0 || ++tried > 4) break;

            var world = TreeWorldgenFixture.Generate(cx, cz, reverse: false,
                radius: TreeGenerator.ReachChunkRadius);
            var highest = new Dictionary<(int X, int Z), (int Y, byte Leaf)>();
            foreach (var ((x, y, z), (block, _)) in world)
            {
                if (x >> Chunk.SizeShift != cx || z >> Chunk.SizeShift != cz || !leafIds.Contains(block)) continue;
                if (!highest.TryGetValue((x, z), out var top) || y > top.Y)
                    highest[(x, z)] = (y, block);
            }

            // The cached canopy itself, not CanopyTop: the edit gate in front of it is a separate
            // rule, and a parallel test may poison columns.
            var canopy = TreeGenerator.GetCanopyColumns(cx, cz);
            for (var lx = 0; lx < Chunk.Size; lx++)
            for (var lz = 0; lz < Chunk.Size; lz++)
            {
                var x = cx * Chunk.Size + lx;
                var z = cz * Chunk.Size + lz;
                var i = (lx << Chunk.SizeShift) | lz;
                var stamped = highest.TryGetValue((x, z), out var leaf) ? leaf.Y : 0;
                Assert.True(stamped == canopy.Tops[i],
                    $"column ({x}, {z}): the chunks stamp their highest leaf at {stamped}, the canopy reads {canopy.Tops[i]}");
                if (stamped == 0) continue;

                Assert.Equal(leaf.Leaf, canopy.LeafBlocks[i]);
                canopied++;
            }
        }

        Assert.True(canopied > 0, "no sampled column inside a cell carried a canopy");
    }
}
