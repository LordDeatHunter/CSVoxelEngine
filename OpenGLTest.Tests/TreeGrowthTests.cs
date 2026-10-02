using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Rules of <see cref="TreeGrowthWriter"/> on stamped trees: a rebuild that grows nothing writes
/// nothing (not even into a mined crown), growth only appends to the tree and never thins it,
/// never writes over a foreign block, never re-attaches a cut branch, writes leaves exactly as
/// leaf decay re-derives them, is deterministic for the same history, advances its stream per
/// call and fires the fluid, decay and far-LOD hooks; a column with no tree plants a rooted
/// sapling only on legal ground under a plantable cell, taking over a ground cover, and plants
/// deterministically. In the LOD column caps collection, because
/// a grow poisons the process-global canopy markers of its columns; every test roots its tree on
/// its own far column.
/// </summary>
[Collection("LOD column caps")]
public sealed class TreeGrowthTests
{
    private const int Seed = 4242;
    private const int BaseX = 500_008, RootY = 1, RootZ = -400_008;
    private const int TestSpacing = 64;

    public TreeGrowthTests() => TestContent.EnsureLoaded();

    private static IEnumerable<TreeSpecies> LeafySpecies => StampedTree.WorldgenSpecies.Where(s => s.HasLeaves);

    private static int RootXOf(int test) => BaseX + test * TestSpacing;

    private static StampedTree StampLoaded(TreeSpecies species, int rootX, int margin = 0)
    {
        var tree = StampedTree.Stamp(species, Seed, rootX, RootY, RootZ);
        var reach = TreeRasterizer.ReachOf(species);
        tree.LoadAround(margin > 0 ? margin : 2 * reach.Horizontal);
        return tree;
    }

    /// <summary>The window a grow can read or write around a root.</summary>
    private static ((int X, int Y, int Z) Min, (int X, int Y, int Z) Max) ReachBox(TreeSpecies species, int rootX)
    {
        var reach = TreeRasterizer.ReachOf(species);
        return ((rootX - reach.Horizontal, RootY - reach.Down - 1, RootZ - reach.Horizontal),
            (rootX + reach.Horizontal, RootY + reach.Up, RootZ + reach.Horizontal));
    }

    private static Dictionary<(int X, int Y, int Z), (byte Id, byte State)> SnapshotReach(
        StampedTree tree, int rootX)
    {
        var (min, max) = ReachBox(tree.Species, rootX);
        return StampedTree.Snapshot(tree.Chunks, min, max);
    }

    private static TreeGrowthResult Grow(TreeGrowthWriter writer, StampedTree tree, int rootX, int steps,
        FluidManager? fluids = null, LeafDecayManager? decay = null) =>
        writer.Grow(tree.Chunks, tree.Species, rootX, RootY, RootZ, steps, Seed, fluids, decay);

    private static TreeGraph Rebuild(StampedTree tree, int rootX)
    {
        var graph = TreeGraph.FromWorld(tree.Chunks.TryGetCell, tree.Species, rootX, RootY, RootZ);
        Assert.NotNull(graph);
        return graph;
    }

    private static HashSet<(int X, int Y, int Z)> CellsOf(TreeGraph graph) =>
        Enumerable.Range(0, graph.Count).Select(i => (graph[i].X, graph[i].Y, graph[i].Z)).ToHashSet();

    private static List<(int X, int Y, int Z)> Diff(
        Dictionary<(int X, int Y, int Z), (byte Id, byte State)> before,
        Dictionary<(int X, int Y, int Z), (byte Id, byte State)> after) =>
        after.Where(kv => !before.TryGetValue(kv.Key, out var b) || b != kv.Value).Select(kv => kv.Key).ToList();

    /// <summary>Clears a stamped tree's limb cell, leaving its subtree's wood standing orphaned.</summary>
    private static (int X, int Y, int Z) CutLimb(StampedTree tree, out HashSet<(int X, int Y, int Z)> orphans)
    {
        var limb = StampedTree.PickLimb(tree.Graph);
        Assert.True(limb > 0, $"{tree.Species.Name} has no limb to cut");
        var cell = ((int)tree.CellOf(limb).X, (int)tree.CellOf(limb).Y, (int)tree.CellOf(limb).Z);
        orphans = StampedTree.SubtreeOf(tree.Graph, limb)
            .Where(i => i != limb)
            .Select(i => ((int)tree.CellOf(i).X, (int)tree.CellOf(i).Y, (int)tree.CellOf(i).Z))
            .ToHashSet();
        tree.Write(cell, 0, 0);
        return cell;
    }

    [Fact]
    public void RebuildingAnIntactTreeRewritesNothing()
    {
        var test = 0;
        foreach (var species in StampedTree.WorldgenSpecies)
        {
            var rootX = RootXOf(test++);
            var tree = StampLoaded(species, rootX);
            var before = SnapshotReach(tree, rootX);

            var result = Grow(new TreeGrowthWriter(), tree, rootX, 0);

            Assert.Equal(TreeGrowthStatus.Grew, result.Status);
            Assert.Equal(0, result.CellsWritten);
            Assert.Empty(Diff(before, SnapshotReach(tree, rootX)));
        }
    }

    [Fact]
    public void RebuildingAMinedCrownRefillsNothing()
    {
        var test = 10;
        foreach (var species in LeafySpecies)
        {
            var rootX = RootXOf(test++);
            var tree = StampLoaded(species, rootX);
            var mined = tree.Leaves.Keys.Where((_, i) => i % 7 == 0).ToList();
            foreach (var cell in mined)
                tree.Write(cell, 0, 0);

            Grow(new TreeGrowthWriter(), tree, rootX, 0);

            foreach (var cell in mined)
                Assert.Equal(((byte)0, (byte)0), tree.Read(cell));
        }
    }

    [Fact]
    public void GrowthAppendsToTheTree()
    {
        var test = 20;
        foreach (var species in LeafySpecies)
        {
            var rootX = RootXOf(test++);
            var tree = StampLoaded(species, rootX);
            var original = Rebuild(tree, rootX);
            var originalCells = CellsOf(original);

            var result = Grow(new TreeGrowthWriter(), tree, rootX, 40);

            Assert.True(result.Succeeded, $"{species.Name}: {result.Status}");
            var grown = Rebuild(tree, rootX);
            Assert.True(originalCells.IsSubsetOf(CellsOf(grown)), $"{species.Name}: an original wood cell left the tree");
            Assert.Equal(original.Count + result.NodesAdded, grown.Count);
        }
    }

    [Fact]
    public void GrowthNeverThinsTheTree()
    {
        var test = 30;
        foreach (var species in LeafySpecies)
        {
            var rootX = RootXOf(test++);
            var tree = StampLoaded(species, rootX);
            CutLimb(tree, out _);
            var before = Rebuild(tree, rootX);
            var codes = Enumerable.Range(0, before.Count).ToDictionary(
                i => (before[i].X, before[i].Y, before[i].Z), i => tree.Read((before[i].X, before[i].Y, before[i].Z)).State);

            Grow(new TreeGrowthWriter(), tree, rootX, 40);

            foreach (var (cell, state) in codes)
            {
                var (id, now) = tree.Read(cell);
                Assert.Equal(species.WoodId, id);
                Assert.True(WoodState.Thickness(now) >= WoodState.Thickness(state),
                    $"{species.Name}: {cell} thinned from {WoodState.Thickness(state)} to {WoodState.Thickness(now)}");
            }
        }
    }

    [Fact]
    public void GrowthNeverOverwritesForeignBlocks()
    {
        var test = 40;
        foreach (var species in LeafySpecies)
        {
            var rootX = RootXOf(test++);
            var tree = StampLoaded(species, rootX);
            var other = LeafySpecies.FirstOrDefault(s => s.LeavesId != species.LeavesId);
            var kinds = new List<(byte Id, byte State)>
            {
                (TerrainGenerator.RockBlock, 0),
                (species.LeavesId, LeafState.Encode(1, true)),
            };
            if (other != null)
                kinds.Add((other.LeavesId, LeafState.Encode(1, false)));

            var (min, max) = ReachBox(species, rootX);
            var sprinkle = new Dictionary<(int X, int Y, int Z), (byte Id, byte State)>();
            foreach (var (cell, value) in StampedTree.Snapshot(tree.Chunks, min, max))
            {
                if (value.Id != 0 || Math.Abs(cell.X + cell.Y + cell.Z) % 5 != 0)
                    continue;
                var kind = kinds[sprinkle.Count % kinds.Count];
                sprinkle[cell] = kind;
                tree.Write(cell, kind.Id, kind.State);
            }

            var writer = new TreeGrowthWriter();
            for (var call = 0; call < 3; call++)
                Grow(writer, tree, rootX, 30);

            foreach (var (cell, value) in sprinkle)
                Assert.Equal(value, tree.Read(cell));
        }
    }

    [Fact]
    public void ACutBranchStaysCutAfterRegrowth()
    {
        var test = 50;
        foreach (var species in LeafySpecies)
        {
            var rootX = RootXOf(test++);
            var tree = StampLoaded(species, rootX);
            var limb = CutLimb(tree, out var orphans);
            var orphanStates = orphans.ToDictionary(c => c, c => tree.Read(c));

            var writer = new TreeGrowthWriter();
            for (var call = 0; call < 3; call++)
                Grow(writer, tree, rootX, 80);

            Assert.NotEqual(species.WoodId, tree.Read(limb).Id);
            foreach (var (cell, value) in orphanStates)
                Assert.Equal(value, tree.Read(cell));
            Assert.Empty(CellsOf(Rebuild(tree, rootX)).Intersect(orphans));
        }
    }

    [Fact]
    public void GrownLeavesAreWhatDecayRederives()
    {
        var test = 60;
        foreach (var species in LeafySpecies)
        {
            var rootX = RootXOf(test++);
            var reach = TreeRasterizer.ReachOf(species);
            var tree = StampLoaded(species, rootX, 3 * reach.Horizontal);
            var before = SnapshotReach(tree, rootX);

            var result = Grow(new TreeGrowthWriter(), tree, rootX, 40);
            Assert.True(result.MeshCellsWritten > 0, $"{species.Name} wrote nothing");
            var grown = SnapshotReach(tree, rootX);
            var written = Diff(before, grown);

            var decay = new LeafDecayManager();
            var drops = new DropManager();
            var (min, max) = ReachBox(species, rootX);
            decay.ScheduleBox(new Vector3(min.X, min.Y, min.Z), new Vector3(max.X, max.Y, max.Z));
            for (var tick = 0; tick < 200 && decay.PendingCells > 0; tick++)
            {
                decay.Update(WorldGenerationConfig.LeafDecayTickSeconds, tree.Chunks, drops);
                while (decay.TickInProgress)
                    decay.Update(0f, tree.Chunks, drops);
            }
            Assert.Equal(0, decay.PendingCells);

            foreach (var cell in written)
                Assert.Equal(grown[cell], tree.Read(cell));

            var graph = Rebuild(tree, rootX);
            foreach (var (cell, value) in SnapshotReach(tree, rootX))
            {
                if (value.Id != species.LeavesId || LeafState.IsPersistent(value.State))
                    continue;
                var expected = Math.Min(LeafCluster.HydrationAt(graph, species, cell.X, cell.Y, cell.Z), LeafState.MaxHydration);
                Assert.True(expected >= 1, $"{species.Name}: leaf {cell} survived decay with no tip holding it");
                Assert.Equal(expected, LeafState.Hydration(value.State));
            }
        }
    }

    [Fact]
    public void GrowthIsDeterministic()
    {
        var test = 70;
        foreach (var species in LeafySpecies)
        {
            var rootX = RootXOf(test++);
            var a = StampLoaded(species, rootX);
            var b = StampLoaded(species, rootX);
            var writerA = new TreeGrowthWriter();
            var writerB = new TreeGrowthWriter();

            for (var call = 0; call < 2; call++)
                Assert.Equal(Grow(writerA, a, rootX, 25), Grow(writerB, b, rootX, 25));

            var snapA = SnapshotReach(a, rootX);
            var snapB = SnapshotReach(b, rootX);
            Assert.Equal(snapA.Count, snapB.Count);
            foreach (var (cell, value) in snapA)
                Assert.Equal(value, snapB[cell]);
        }
    }

    [Fact]
    public void RepeatedCallsUseDifferentStreams()
    {
        var seeds = Enumerable.Range(0, 16).Select(call => TreeGrowthWriter.GrowSeed(0x1234_5678UL, 7, (ulong)call));
        Assert.Equal(16, seeds.Distinct().Count());

        // A young tree: a lone root cell, which every grow can extend
        var test = 80;
        foreach (var species in LeafySpecies)
        {
            var rootX = RootXOf(test++);
            var chunks = new ChunkManager(null!);
            var reach = TreeRasterizer.ReachOf(species);
            StampedTree.LoadAround(chunks, [(rootX, RootY, RootZ)], 2 * reach.Horizontal);
            var root = new TreeGraph(rootX, RootY, RootZ, species.TipPx);
            StampedTree.WriteCell(chunks, (rootX, RootY, RootZ), species.WoodId, TreeRasterizer.WoodStateOf(in root[TreeGraph.Root]));

            var writer = new TreeGrowthWriter();
            for (var call = 0; call < 2; call++)
            {
                var result = writer.Grow(chunks, species, rootX, RootY, RootZ, 6, Seed);
                Assert.Equal(TreeGrowthStatus.Grew, result.Status);
                Assert.True(result.NodesAdded > 0, $"{species.Name}: grow {call} added nothing");
            }
        }
    }

    [Fact]
    public void GrowthFiresTheEditHooks()
    {
        var test = 90;
        foreach (var species in LeafySpecies)
        {
            var rootX = RootXOf(test++);
            var tree = StampLoaded(species, rootX);
            var before = SnapshotReach(tree, rootX);
            var fluids = new FluidManager();
            var decay = new LeafDecayManager();

            var result = Grow(new TreeGrowthWriter(), tree, rootX, 40, fluids, decay);

            Assert.True(result.MeshCellsWritten > 0, $"{species.Name} wrote nothing");
            Assert.True(fluids.PendingCells > 0, $"{species.Name}: no fluid cell scheduled");
            Assert.True(decay.PendingCells > 0, $"{species.Name}: no decay cell scheduled");

            var after = SnapshotReach(tree, rootX);
            var idChanged = after.Where(kv => before[kv.Key].Id != kv.Value.Id).Select(kv => (kv.Key.X, kv.Key.Z)).ToHashSet();
            Assert.NotEmpty(idChanged);
            Assert.True(TreeGenerator.ExportCanopyDisturbed().Any(idChanged.Contains),
                $"{species.Name}: no grown column reached the far-LOD canopy markers");
        }

        // A refused grow: an all-air window holds no root and no ground
        {
            var species = LeafySpecies.First();
            var rootX = RootXOf(test);
            var chunks = new ChunkManager(null!);
            var reach = TreeRasterizer.ReachOf(species);
            StampedTree.LoadAround(chunks, [(rootX, RootY, RootZ)], 2 * reach.Horizontal);
            var (min, max) = ReachBox(species, rootX);
            var before = StampedTree.Snapshot(chunks, min, max);
            var fluids = new FluidManager();
            var decay = new LeafDecayManager();

            var result = new TreeGrowthWriter().Grow(chunks, species, rootX, RootY, RootZ, 40, Seed, fluids, decay);

            Assert.False(result.Succeeded);
            Assert.Equal(0, fluids.PendingCells);
            Assert.Equal(0, decay.PendingCells);
            Assert.Empty(Diff(before, StampedTree.Snapshot(chunks, min, max)));
        }
    }

    // --- Planting: a column with no tree roots a sapling on its ground ---

    /// <summary>A loaded, empty area around a column with a one-cell floor of
    /// <paramref name="floorId"/> under the root level, wide enough for the whole reach.</summary>
    private static ChunkManager FloorWorld(TreeSpecies species, int rootX, byte floorId)
    {
        var chunks = new ChunkManager(null!);
        var reach = TreeRasterizer.ReachOf(species);
        StampedTree.LoadAround(chunks, [(rootX, RootY, RootZ), (rootX, RootY + reach.Up, RootZ)], 2 * reach.Horizontal);
        StampedTree.Fill(chunks, (rootX - reach.Horizontal, RootY - 1, RootZ - reach.Horizontal),
            (rootX + reach.Horizontal, RootY - 1, RootZ + reach.Horizontal), floorId);
        return chunks;
    }

    private static Dictionary<(int X, int Y, int Z), (byte Id, byte State)> SnapshotReach(
        ChunkManager chunks, TreeSpecies species, int rootX)
    {
        var (min, max) = ReachBox(species, rootX);
        return StampedTree.Snapshot(chunks, min, max);
    }

    private static (byte Id, byte State) ReadCell(ChunkManager chunks, int x, int y, int z)
    {
        Assert.True(chunks.TryGetCell(x, y, z, out var id, out var state));
        return (id, state);
    }

    // A root state for a lone wood cell, as the rasteriser writes it
    private static byte LoneRootState(TreeSpecies species)
    {
        var lone = new TreeGraph(0, 0, 0, species.TipPx);
        return TreeRasterizer.WoodStateOf(in lone[TreeGraph.Root]);
    }

    [Fact]
    public void APlantedSaplingIsARootedTree()
    {
        var test = 100;
        foreach (var species in StampedTree.WorldgenSpecies)
        {
            var rootX = RootXOf(test++);
            var chunks = FloorWorld(species, rootX, TerrainGenerator.SurfaceBlock);

            var result = new TreeGrowthWriter().Grow(chunks, species, rootX, RootY, RootZ, 20, Seed);

            Assert.Equal(TreeGrowthStatus.Planted, result.Status);
            Assert.Equal((rootX, RootY, RootZ), (result.RootX, result.RootY, result.RootZ));
            var (id, state) = ReadCell(chunks, rootX, RootY, RootZ);
            Assert.Equal(species.WoodId, id);
            Assert.True(WoodState.IsRoot(state), $"{species.Name}: the planted cell is not a root");
            var graph = TreeGraph.FromWorld(chunks.TryGetCell, species, rootX, RootY, RootZ);
            Assert.NotNull(graph);
            Assert.Equal(1 + result.NodesAdded, graph.Count);
        }
    }

    [Fact]
    public void SaplingsRespectTheSoilRule()
    {
        var test = 120;
        foreach (var species in StampedTree.WorldgenSpecies)
        {
            var rootX = RootXOf(test++);
            var chunks = FloorWorld(species, rootX, TerrainGenerator.RockBlock);
            var before = SnapshotReach(chunks, species, rootX);

            var result = new TreeGrowthWriter().Grow(chunks, species, rootX, RootY, RootZ, 20, Seed);

            if (species.Worldgen!.MinSoil > 0)
            {
                Assert.Equal(TreeGrowthStatus.NoGround, result.Status);
                Assert.Empty(Diff(before, SnapshotReach(chunks, species, rootX)));
            }
            else
            {
                // A species that needs no soil roots on any solid ground
                Assert.Equal(TreeGrowthStatus.Planted, result.Status);
            }
        }
    }

    [Fact]
    public void ASaplingNeedsAPlantableRootCell()
    {
        var test = 140;
        foreach (var species in StampedTree.WorldgenSpecies)
        {
            var blockers = new List<(byte Id, byte State)>();
            if (species.HasLeaves)
                blockers.Add((species.LeavesId, LeafState.Encode(1, true)));
            var other = StampedTree.WorldgenSpecies.FirstOrDefault(s => s.WoodId != species.WoodId);
            if (other != null)
                blockers.Add((other.WoodId, LoneRootState(other)));
            Assert.NotEmpty(blockers);

            foreach (var blocker in blockers)
            {
                var rootX = RootXOf(test++);
                var chunks = FloorWorld(species, rootX, TerrainGenerator.SurfaceBlock);
                StampedTree.WriteCell(chunks, (rootX, RootY, RootZ), blocker.Id, blocker.State);
                var before = SnapshotReach(chunks, species, rootX);

                var result = new TreeGrowthWriter().Grow(chunks, species, rootX, RootY, RootZ, 20, Seed);

                Assert.Equal(TreeGrowthStatus.NoGround, result.Status);
                Assert.Empty(Diff(before, SnapshotReach(chunks, species, rootX)));
            }
        }
    }

    [Fact]
    public void ASaplingTakesOverGroundCover()
    {
        var cover = Enumerable.Range(1, 255)
            .Where(id => BlockType.ById[id] != null)
            .SelectMany(id => Enumerable.Range(0, 256).Select(state => (Id: (byte)id, State: (byte)state)))
            .First(c => BlockType.IsReplaceableByPlacement(c.Id, c.State)
                && !TreeGenerator.IsLeaf(c.Id) && !BlockType.IsTreeWood(c.Id));

        var test = 170;
        foreach (var species in StampedTree.WorldgenSpecies)
        {
            var rootX = RootXOf(test++);
            var chunks = FloorWorld(species, rootX, TerrainGenerator.SurfaceBlock);
            StampedTree.WriteCell(chunks, (rootX, RootY, RootZ), cover.Id, cover.State);

            var result = new TreeGrowthWriter().Grow(chunks, species, rootX, RootY, RootZ, 20, Seed);

            Assert.Equal(TreeGrowthStatus.Planted, result.Status);
            var (id, state) = ReadCell(chunks, rootX, RootY, RootZ);
            Assert.Equal(species.WoodId, id);
            Assert.True(WoodState.IsRoot(state), $"{species.Name}: the cover cell does not hold the root");
        }
    }

    [Fact]
    public void PlantingIsDeterministic()
    {
        var test = 190;
        foreach (var species in StampedTree.WorldgenSpecies)
        {
            var rootX = RootXOf(test++);
            var a = FloorWorld(species, rootX, TerrainGenerator.SurfaceBlock);
            var b = FloorWorld(species, rootX, TerrainGenerator.SurfaceBlock);
            var writerA = new TreeGrowthWriter();
            var writerB = new TreeGrowthWriter();

            for (var call = 0; call < 2; call++)
                Assert.Equal(writerA.Grow(a, species, rootX, RootY, RootZ, 15, Seed),
                    writerB.Grow(b, species, rootX, RootY, RootZ, 15, Seed));

            var snapA = SnapshotReach(a, species, rootX);
            var snapB = SnapshotReach(b, species, rootX);
            Assert.Equal(snapA.Count, snapB.Count);
            foreach (var (cell, value) in snapA)
                Assert.Equal(value, snapB[cell]);
        }
    }
}
