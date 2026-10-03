using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Rules of <see cref="LeafDecayManager"/>: a scheduled leaf re-derives its hydration from the
/// live tips of its species around it, so an intact canopy reads back bit for bit and schedules
/// nothing further, a leaf whose tips are gone decays over a bounded number of ticks (and no
/// faster than the per-tick budget allows), rolling its loot; a leaf held by another live tip
/// survives, a persistent leaf never decays, an unloaded neighbour freezes a cell instead of
/// decaying it, and thin wood orphaned by a cut crumbles outward while the connected rest stands.
/// Grown trees are stamped through <see cref="StampedTree"/>. In the LOD column caps collection,
/// because a decay poisons the process-global felled-canopy markers of its column; the
/// coordinates sit far from every column another test reads.
/// </summary>
[Collection("LOD column caps")]
public sealed class LeafDecayTests
{
    private const int Seed = 4242;
    private const int RootX = 400_008, RootY = 1, RootZ = -300_008;

    // Hand-built cells sit in the chunk holding the root column, at local x 0 so a cell can sit
    // on that chunk's -X border
    private const int BaseX = RootX & ~Chunk.SizeMask, BaseZ = RootZ;

    public LeafDecayTests() => TestContent.EnsureLoaded();

    private static IEnumerable<TreeSpecies> LeafySpecies => StampedTree.WorldgenSpecies.Where(s => s.HasLeaves);

    /// <summary>Runs exactly <paramref name="ticks"/> whole ticks: one tick's worth of time
    /// starts each, and zero-time frames finish its slices without letting the next one start
    /// (a frame that finishes a tick with time in hand would otherwise begin another).</summary>
    private static void RunTicks(LeafDecayManager m, ChunkManager c, DropManager d, int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            var before = m.TickIndex;
            m.Update(WorldGenerationConfig.LeafDecayTickSeconds, c, d);
            while (m.TickInProgress)
                m.Update(0f, c, d);
            Assert.True(m.TickIndex == before + 1, "a tick's worth of time did not run exactly one tick");
        }
    }

    /// <summary>Ticks enough for <paramref name="removals"/> cells under the per-tick budget,
    /// plus the tick that finds them and one of slack.</summary>
    private static int Bound(int removals) => removals / WorldGenerationConfig.LeafDecayMaxRemovalsPerTick + 2;

    /// <summary>The graph rule's live tips among the nodes still standing: childless (among
    /// the remaining nodes), non-feature and no thicker than the species' tip limit.</summary>
    private static List<int> LiveTipsAmong(TreeGraph g, TreeSpecies s, HashSet<int> remaining)
    {
        var hasChild = new HashSet<int>();
        foreach (var j in remaining)
            if (g[j].Parent >= 0 && remaining.Contains(g[j].Parent))
                hasChild.Add(g[j].Parent);

        return remaining
            .Where(i => !g[i].IsFeature && g[i].ThicknessPx <= s.TipMaxPx && !hasChild.Contains(i))
            .ToList();
    }

    private static int ExpectedHydration(TreeGraph g, TreeSpecies s, List<int> tips, (int X, int Y, int Z) cell)
    {
        // The root is never cut away, so the tree's cluster radius still reads from g
        var radius = LeafCluster.ClusterRadius(s, g);
        var best = 0;
        foreach (var t in tips)
            best = Math.Max(best, LeafCluster.HydrationFromTip(s, radius, cell.X - g[t].X, cell.Y - g[t].Y, cell.Z - g[t].Z));
        return Math.Min(best, LeafState.MaxHydration);
    }

    /// <summary>Flat ground grows no root walk, which is what makes the graph's live tips and
    /// the world's live tips the same cells.</summary>
    private static void AssertNoFeatureNodes(StampedTree tree)
    {
        for (var i = 0; i < tree.Graph.Count; i++)
            Assert.False(tree.Graph[i].IsFeature,
                $"{tree.Species.Name}: node {i} is a feature node on flat ground; the graph rule no longer matches the world rule");
    }

    /// <summary>Stamps the species and loads empty chunks over its cells grown by twice the
    /// schedule reach, so no decision the facts expect is frozen by an unloaded scan box.</summary>
    private static StampedTree StampLoaded(TreeSpecies species, LeafDecayManager manager)
    {
        var tree = StampedTree.Stamp(species, Seed, RootX, RootY, RootZ);
        tree.LoadAround(2 * manager.ScheduleReach);
        return tree;
    }

    private static bool IsAir(ChunkManager chunks, (int X, int Y, int Z) cell) =>
        chunks.TryGetCell(cell.X, cell.Y, cell.Z, out var id, out _) && id == 0;

    /// <summary>A free cell face-adjacent to some wood cell of the tree, off the leaves and
    /// above the ground.</summary>
    private static (int X, int Y, int Z) FreeCellBesideWood(StampedTree tree)
    {
        foreach (var (x, y, z) in tree.Wood.Keys.OrderBy(k => k.Y).ThenBy(k => k.X).ThenBy(k => k.Z))
            for (var dir = 0; dir < TreeDirections.Count; dir++)
            {
                var cell = (x + TreeDirections.Dx[dir], y + TreeDirections.Dy[dir], z + TreeDirections.Dz[dir]);
                if (cell.Item2 >= RootY && !tree.Wood.ContainsKey(cell) && !tree.Leaves.ContainsKey(cell))
                    return cell;
            }

        throw new InvalidOperationException($"{tree.Species.Name} has no free cell beside its wood");
    }

    /// <summary>A leaf between two thin tips over full-cube roots, all in one chunk: the offset
    /// (±1, 0, 0) from either tip lies inside both the blob and the cone cluster.</summary>
    private static (ChunkManager Chunks, (int X, int Y, int Z) Leaf, (int X, int Y, int Z) TipA, (int X, int Y, int Z) TipB)
        TwoTips(TreeSpecies s, byte leafState)
    {
        var chunks = new ChunkManager(null!);
        var leaf = (BaseX + 8, 8, BaseZ);
        var tipA = (BaseX + 7, 8, BaseZ);
        var tipB = (BaseX + 9, 8, BaseZ);
        var tipState = WoodState.Encode(0, TreeDirections.Down, false);
        var rootState = WoodState.Encode(WoodState.FullCube, WoodState.ParentRoot, false);

        StampedTree.WriteCell(chunks, leaf, s.LeavesId, leafState);
        foreach (var tip in new[] { tipA, tipB })
        {
            StampedTree.WriteCell(chunks, tip, s.WoodId, tipState);
            StampedTree.WriteCell(chunks, (tip.Item1, tip.Item2 - 1, tip.Item3), s.WoodId, rootState);
        }

        return (chunks, leaf, tipA, tipB);
    }

    private static void Clear(ChunkManager chunks, (int X, int Y, int Z) tip)
    {
        StampedTree.WriteCell(chunks, tip, 0, 0);
        StampedTree.WriteCell(chunks, (tip.X, tip.Y - 1, tip.Z), 0, 0);
    }

    [Fact]
    public void AnIntactCanopyKeepsEveryLeafAndThenSchedulesNothing()
    {
        foreach (var species in StampedTree.WorldgenSpecies)
        {
            var manager = new LeafDecayManager();
            var drops = new DropManager();
            var tree = StampLoaded(species, manager);
            AssertNoFeatureNodes(tree);

            var (min, max) = StampedTree.Bounds(tree.Wood.Keys.Concat(tree.Leaves.Keys));
            manager.ScheduleBox(min, max);

            var guard = manager.TickIndex + 32;
            while (manager.PendingCells > 0)
            {
                Assert.True(manager.TickIndex < guard, $"{species.Name}: an intact tree never settled");
                RunTicks(manager, tree.Chunks, drops, 1);
            }

            void AssertIntact(string when)
            {
                foreach (var (cell, expected) in tree.Wood.Concat(tree.Leaves))
                    Assert.True(expected == tree.Read(cell),
                        $"{species.Name}: {cell} changed {when}: stamped {expected}, now {tree.Read(cell)}");
                Assert.True(drops.Drops.Count == 0, $"{species.Name}: an intact tree dropped loot {when}");
            }

            AssertIntact("after its canopy was re-derived");

            RunTicks(manager, tree.Chunks, drops, 1);
            Assert.True(manager.PendingCells == 0, $"{species.Name}: a settled canopy scheduled more work");
            AssertIntact("on a settled tick");
        }
    }

    [Fact]
    public void AYoungOrMatureCanopyIsExactlyWhatDecayRederives()
    {
        // Every leafy species, worldgen or not: the cluster radius follows the trunk, and the
        // world side has to re-derive that radius from the root the tip's chain ends at
        foreach (var species in TreeSpeciesRegistry.All.Where(s => s.HasLeaves).OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            var young = StampedTree.YoungStep(species, Seed, RootX, RootY, RootZ);
            if (species.LeafMinRadius < species.LeafRadius)
                Assert.True(young != null, $"{species.Name}: no growth step wears a crown below its full radius");

            foreach (var steps in young is { } y ? new int?[] { y, null } : new int?[] { null })
            {
                var what = $"{species.Name} at {(steps is { } n ? $"step {n}" : "maturity")}";
                var manager = new LeafDecayManager();
                var drops = new DropManager();
                var tree = StampedTree.Stamp(species, Seed, RootX, RootY, RootZ, steps);
                tree.LoadAround(2 * manager.ScheduleReach);
                Assert.True(tree.Leaves.Count > 0, $"{what}: the stamped tree has no leaves to re-derive");

                var (min, max) = StampedTree.Bounds(tree.Wood.Keys.Concat(tree.Leaves.Keys));
                manager.ScheduleBox(min, max);
                var guard = manager.TickIndex + 32;
                while (manager.PendingCells > 0)
                {
                    Assert.True(manager.TickIndex < guard, $"{what}: the canopy never settled");
                    RunTicks(manager, tree.Chunks, drops, 1);
                }

                foreach (var (cell, expected) in tree.Wood.Concat(tree.Leaves))
                    Assert.True(expected == tree.Read(cell),
                        $"{what}: {cell} changed under decay: stamped {expected}, now {tree.Read(cell)}");
                Assert.True(drops.Drops.Count == 0, $"{what}: decay removed part of an intact tree");
            }
        }
    }

    [Fact]
    public void CuttingAwayATipsBranchDecaysItsLeavesOverSeveralTicks()
    {
        var checkedAny = false;
        var decayedAny = false;
        foreach (var species in LeafySpecies)
        {
            var manager = new LeafDecayManager();
            var drops = new DropManager();
            var tree = StampLoaded(species, manager);
            AssertNoFeatureNodes(tree);

            var limb = StampedTree.PickLimb(tree.Graph);
            if (limb < 0)
                continue;
            checkedAny = true;

            var subtree = StampedTree.SubtreeOf(tree.Graph, limb);
            var subtreeCells = subtree.Select(i => (tree.Graph[i].X, tree.Graph[i].Y, tree.Graph[i].Z)).ToList();
            foreach (var cell in subtreeCells)
                tree.Write(cell, 0, 0);

            var remaining = Enumerable.Range(0, tree.Graph.Count).Where(i => !subtree.Contains(i)).ToHashSet();
            var tips = LiveTipsAmong(tree.Graph, species, remaining);
            var expected = tree.Leaves.Keys.ToDictionary(c => c, c => ExpectedHydration(tree.Graph, species, tips, c));
            var doomed = expected.Where(e => e.Value == 0).Select(e => e.Key).ToList();

            var (min, max) = StampedTree.Bounds(subtreeCells);
            manager.ScheduleBox(min, max);

            if (doomed.Count > WorldGenerationConfig.LeafDecayMaxRemovalsPerTick)
            {
                RunTicks(manager, tree.Chunks, drops, 1);
                Assert.True(doomed.Any(c => !IsAir(tree.Chunks, c)),
                    $"{species.Name}: {doomed.Count} leaves decayed in a single tick, past the per-tick budget");
            }

            RunTicks(manager, tree.Chunks, drops, Bound(doomed.Count));

            foreach (var (cell, hydration) in expected)
            {
                var (id, state) = tree.Read(cell);
                if (hydration == 0)
                {
                    Assert.True(id == 0, $"{species.Name}: the leaf at {cell} lost its tip but still stands");
                    continue;
                }

                Assert.True(id == species.LeavesId, $"{species.Name}: the leaf at {cell} still held by a tip is gone");
                Assert.False(LeafState.IsPersistent(state), $"{species.Name}: the leaf at {cell} turned persistent");
                Assert.True(LeafState.Hydration(state) == hydration,
                    $"{species.Name}: the leaf at {cell} holds hydration {LeafState.Hydration(state)}, its tips give {hydration}");
            }

            foreach (var i in remaining)
            {
                var cell = (tree.Graph[i].X, tree.Graph[i].Y, tree.Graph[i].Z);
                Assert.True(tree.Wood[cell] == tree.Read(cell), $"{species.Name}: connected wood at {cell} changed");
            }

            decayedAny |= doomed.Count > 0;
        }

        Assert.True(checkedAny, "no leafy worldgen species grew a limb with children");
        Assert.True(decayedAny, "no limb cut left a single leaf without a tip");
    }

    [Fact]
    public void LeavesHeldByAnotherLiveTipSurvive()
    {
        foreach (var species in LeafySpecies)
        {
            var h = species.LeafHydration;
            Assert.True(h >= 2, $"{species.Name}: hydration {h} leaves no room for a leaf beside a tip");

            var manager = new LeafDecayManager();
            var drops = new DropManager();
            var (chunks, leaf, tipA, tipB) = TwoTips(species, LeafState.Encode(h - 1, false));

            Clear(chunks, tipA);
            manager.ScheduleAround(tipA.Item1, tipA.Item2, tipA.Item3);
            RunTicks(manager, chunks, drops, Bound(1));

            Assert.True(chunks.TryGetCell(leaf.Item1, leaf.Item2, leaf.Item3, out var id, out var state));
            Assert.True(id == species.LeavesId, $"{species.Name}: a leaf beside a second live tip decayed");
            Assert.True(LeafState.Hydration(state) == h - 1 && !LeafState.IsPersistent(state),
                $"{species.Name}: the leaf held by the second tip reads state {state}");

            Clear(chunks, tipB);
            manager.ScheduleAround(tipB.Item1, tipB.Item2, tipB.Item3);
            RunTicks(manager, chunks, drops, Bound(1));

            Assert.True(IsAir(chunks, leaf), $"{species.Name}: the leaf survived losing both tips");
        }
    }

    [Fact]
    public void PersistentLeavesNeverDecay()
    {
        foreach (var species in LeafySpecies)
        {
            var persistent = LeafState.Encode(0, true);

            // Alone, with no wood anywhere
            var manager = new LeafDecayManager();
            var drops = new DropManager();
            var chunks = new ChunkManager(null!);
            var cell = (BaseX + 8, 8, BaseZ);
            StampedTree.WriteCell(chunks, cell, species.LeavesId, persistent);
            manager.ScheduleAround(cell.Item1, cell.Item2, cell.Item3);
            RunTicks(manager, chunks, drops, Bound(1) + 2);

            Assert.True(chunks.TryGetCell(cell.Item1, cell.Item2, cell.Item3, out var id, out var state));
            Assert.True(id == species.LeavesId && state == persistent,
                $"{species.Name}: a lone persistent leaf became ({id}, {state})");
            Assert.True(drops.Drops.Count == 0, $"{species.Name}: a persistent leaf dropped loot");

            // Beside a live tip, whose hydration it would otherwise take: never rewritten
            var held = TwoTips(species, persistent);
            manager.ScheduleAround(held.Leaf.Item1, held.Leaf.Item2, held.Leaf.Item3);
            RunTicks(manager, held.Chunks, drops, Bound(1) + 2);

            Assert.True(held.Chunks.TryGetCell(held.Leaf.Item1, held.Leaf.Item2, held.Leaf.Item3, out id, out state));
            Assert.True(id == species.LeavesId && state == persistent,
                $"{species.Name}: a persistent leaf beside a tip was rewritten to ({id}, {state})");
        }
    }

    [Fact]
    public void AnUnloadedNeighbourFreezesTheCell()
    {
        var cell = (X: BaseX, Y: 8, Z: BaseZ);
        var cx = cell.X >> Chunk.SizeShift;
        var cz = cell.Z >> Chunk.SizeShift;

        void AssertFreezesThenGoes(string what, byte id, byte state)
        {
            var manager = new LeafDecayManager();
            var drops = new DropManager();
            var chunks = new ChunkManager(null!);
            StampedTree.WriteCell(chunks, cell, id, state);

            manager.ScheduleAround(cell.X, cell.Y, cell.Z);
            RunTicks(manager, chunks, drops, Bound(1) + 2);

            Assert.True(chunks.TryGetCell(cell.X, cell.Y, cell.Z, out var now, out var nowState));
            Assert.True(now == id && nowState == state, $"{what} beside an unloaded chunk became ({now}, {nowState})");
            Assert.True(drops.Drops.Count == 0, $"{what} beside an unloaded chunk dropped loot");
            Assert.True(manager.PendingCells == 0, $"{what} beside an unloaded chunk kept rescheduling itself");

            StampedTree.EnsureChunk(chunks, cx - 1, 0, cz);
            manager.ScheduleAround(cell.X, cell.Y, cell.Z);
            RunTicks(manager, chunks, drops, Bound(1));

            Assert.True(IsAir(chunks, cell), $"{what} still stands once its neighbourhood loaded");
        }

        foreach (var species in LeafySpecies)
            AssertFreezesThenGoes($"{species.Name}: a tipless leaf", species.LeavesId, LeafState.Encode(0, false));

        // Parent across the -X border, in the absent chunk
        foreach (var species in StampedTree.WorldgenSpecies)
            AssertFreezesThenGoes($"{species.Name}: thin wood", species.WoodId, WoodState.Encode(0, 2, false));
    }

    [Fact]
    public void DecayRollsTheLeafLoot()
    {
        foreach (var species in LeafySpecies)
        {
            var manager = new LeafDecayManager();
            var drops = new DropManager();
            var tree = StampLoaded(species, manager);

            foreach (var cell in tree.Wood.Keys)
                tree.Write(cell, 0, 0);
            var (min, max) = StampedTree.Bounds(tree.Wood.Keys.Concat(tree.Leaves.Keys));
            manager.ScheduleBox(min, max);

            // A whole crown dissolves over several ticks, never past the per-tick budget
            if (tree.Leaves.Count > WorldGenerationConfig.LeafDecayMaxRemovalsPerTick)
            {
                RunTicks(manager, tree.Chunks, drops, 1);
                Assert.True(tree.Leaves.Keys.Any(c => !IsAir(tree.Chunks, c)),
                    $"{species.Name}: {tree.Leaves.Count} leaves decayed in a single tick, past the per-tick budget");
            }

            RunTicks(manager, tree.Chunks, drops, Bound(tree.Leaves.Count));

            foreach (var cell in tree.Leaves.Keys)
                Assert.True(IsAir(tree.Chunks, cell), $"{species.Name}: the leaf at {cell} outlived every tip");

            var possible = new List<byte>();
            for (var i = 0; i < 400; i++)
                DropManager.RollLoot(species.LeavesId, new LootContext(0, 0, 0), possible);
            var possibleIds = possible.ToHashSet();
            foreach (var drop in drops.Drops)
                Assert.True(possibleIds.Contains(drop.BlockId),
                    $"{species.Name}: a decayed leaf dropped {drop.BlockId}, which its loot never rolls");

            if (tree.Leaves.Count > 50)
                Assert.True(drops.Drops.Count > 0, $"{species.Name}: {tree.Leaves.Count} leaves decayed without a single drop");
        }
    }

    [Fact]
    public void DecayedLeavesScheduleNothingFurther()
    {
        foreach (var species in LeafySpecies)
        {
            var manager = new LeafDecayManager();
            var drops = new DropManager();
            var tree = StampLoaded(species, manager);

            foreach (var cell in tree.Wood.Keys)
                tree.Write(cell, 0, 0);
            var (min, max) = StampedTree.Bounds(tree.Wood.Keys.Concat(tree.Leaves.Keys));
            manager.ScheduleBox(min, max);

            // A leaf feeds no other cell, so after any tick the only work left is the doomed
            // leaves the per-tick budget held back
            var guard = Bound(tree.Leaves.Count) + 2;
            for (var t = 0; t < guard && manager.PendingCells > 0; t++)
            {
                RunTicks(manager, tree.Chunks, drops, 1);
                var standing = tree.Leaves.Keys.Count(c => !IsAir(tree.Chunks, c));
                Assert.True(manager.PendingCells <= standing,
                    $"{species.Name}: {manager.PendingCells} cells pending with only {standing} leaves left to decay");
            }

            Assert.True(manager.PendingCells == 0, $"{species.Name}: the decay never settled");
        }
    }

    [Fact]
    public void AWholeTreesBoxIsNeverTruncatedByTheBacklog()
    {
        // The box a fell schedules is at most the tree's reach on every side; it must be queued
        // whole, or the far side of a felled crown would never decay
        var manager = new LeafDecayManager();
        var reach = manager.ScheduleReach;
        foreach (var species in StampedTree.WorldgenSpecies)
        {
            var (h, up, down) = TreeRasterizer.ReachOf(species);
            manager.ScheduleBox(new Vector3(-h, -down, -h), new Vector3(h, up, h));

            long side = 2 * h + 1 + 2 * reach;
            long height = up + down + 1 + 2 * reach;
            Assert.True(manager.PendingCells == side * side * height,
                $"{species.Name}: a whole tree's box queued {manager.PendingCells} of {side * side * height} cells");
            RunTicks(manager, new ChunkManager(null!), new DropManager(), 1);
        }
    }

    [Fact]
    public void OrphanedThinWoodCrumblesWhileTheConnectedRestStays()
    {
        var checkedAny = false;
        foreach (var species in StampedTree.WorldgenSpecies)
        {
            var manager = new LeafDecayManager();
            var drops = new DropManager();
            var tree = StampLoaded(species, manager);

            var limb = StampedTree.PickLimb(tree.Graph);
            if (limb < 0)
                continue;
            checkedAny = true;

            var subtree = StampedTree.SubtreeOf(tree.Graph, limb);
            var limbCell = (tree.Graph[limb].X, tree.Graph[limb].Y, tree.Graph[limb].Z);
            var detached = FreeCellBesideWood(tree);
            tree.Write(detached, species.WoodId, WoodState.Detached);
            tree.Write(limbCell, 0, 0);

            manager.ScheduleAround(limbCell.Item1, limbCell.Item2, limbCell.Item3);
            // One layer per tick outward, under a budget the decaying leaves share
            RunTicks(manager, tree.Chunks, drops, subtree.Count + Bound(subtree.Count + tree.Leaves.Count));

            for (var i = 0; i < tree.Graph.Count; i++)
            {
                var cell = (tree.Graph[i].X, tree.Graph[i].Y, tree.Graph[i].Z);
                if (subtree.Contains(i))
                    Assert.True(IsAir(tree.Chunks, cell), $"{species.Name}: orphaned wood at {cell} still stands");
                else
                    Assert.True(tree.Wood[cell] == tree.Read(cell), $"{species.Name}: connected wood at {cell} changed");
            }

            Assert.True(tree.Read(detached) == (species.WoodId, WoodState.Detached),
                $"{species.Name}: detached wood beside the tree crumbled");
        }

        Assert.True(checkedAny, "no worldgen species grew a limb with children");
    }

    /// <summary>Stamps the species loaded wide enough for every scan box and support search, with
    /// real ground (rock) in every free cell one below the root across the tree's footprint, so
    /// a wide trunk's root-level shells stand on ground exactly as they would in the world.</summary>
    private static StampedTree StampGrounded(TreeSpecies species, LeafDecayManager manager)
    {
        var tree = StampedTree.Stamp(species, Seed, RootX, RootY, RootZ);
        tree.LoadAround(Math.Max(2 * manager.ScheduleReach, TreeRasterizer.MaxShellRadius + 2));
        var (min, max) = (tree.Min, tree.Max);
        for (var x = min.X - 1; x <= max.X + 1; x++)
        for (var z = min.Z - 1; z <= max.Z + 1; z++)
        {
            var cell = (x, RootY - 1, z);
            if (!tree.Wood.ContainsKey(cell) && !tree.Leaves.ContainsKey(cell))
                tree.Write(cell, TerrainGenerator.RockBlock, 0);
        }

        return tree;
    }

    /// <summary>
    /// The cells of the lowest shell-ringed trunk slice above the root (whose slices below and
    /// above are ringed too) in cut order, with one on-axis shell standing in all three slices
    /// last: cutting all but that last cell leaves the slice partly standing, cutting all of
    /// them severs it whole.
    /// </summary>
    private static List<(int X, int Y, int Z)> SliceCuts(StampedTree tree)
    {
        var chain = new List<int>();
        TreeRasterizer.TrunkChain(tree.Graph, chain);
        for (var i = 1; i + 1 < chain.Count; i++)
        {
            var core = tree.Graph[chain[i]];
            if (TreeRasterizer.BlockRadius(core.ThicknessPx) < 1
                || TreeRasterizer.BlockRadius(tree.Graph[chain[i + 1]].ThicknessPx) < 1)
                continue;

            for (var dir = 0; dir < TreeDirections.Count; dir++)
            {
                if (TreeDirections.IsVertical(dir))
                    continue;
                var (ox, oz) = (core.X + TreeDirections.Dx[dir], core.Z + TreeDirections.Dz[dir]);
                var column = Enumerable.Range(core.Y - 1, 3).Select(y => (ox, y, oz)).ToList();
                if (!column.All(c => tree.Wood.TryGetValue(c, out var w) && WoodState.IsShell(w.State)))
                    continue;

                var kept = column[1];
                return tree.Wood.Keys.Where(k => k.Y == core.Y && k != kept)
                    .OrderBy(k => k.X).ThenBy(k => k.Z).Append(kept).ToList();
            }
        }

        throw new InvalidOperationException($"{tree.Species.Name} has no shell-ringed slice above its root");
    }

    /// <summary>The wood a sequence of fells drops besides the cut cells themselves: each cut is
    /// felled through <see cref="TopplingTree.Collect"/> and everything it took is cleared, as
    /// <c>World.FellWood</c> would, before the next cut.</summary>
    private static HashSet<(int X, int Y, int Z)> FelledBy(StampedTree tree, List<(int X, int Y, int Z)> cuts)
    {
        var felled = new HashSet<(int X, int Y, int Z)>();
        foreach (var cut in cuts)
        {
            var (id, state) = tree.Read(cut);
            if (!BlockType.IsTreeWood(id))
                continue;

            foreach (var block in TopplingTree.Collect(tree.Chunks, new Vector3(cut.X, cut.Y, cut.Z), id, state))
            {
                if (!BlockType.IsTreeWood(block.Id))
                    continue;
                var cell = ((int)block.Cell.X, (int)block.Cell.Y, (int)block.Cell.Z);
                felled.Add(cell);
                tree.Write(cell, 0, 0);
            }
        }

        felled.ExceptWith(cuts);
        return felled;
    }

    /// <summary>Runs ticks until nothing is scheduled any more, failing past a bound that covers
    /// one crumbled layer per tick plus every removal under the per-tick budget.</summary>
    private static void RunUntilSettled(LeafDecayManager m, StampedTree tree, DropManager d, string what)
    {
        var guard = m.TickIndex + tree.Wood.Count + Bound(tree.Wood.Count + tree.Leaves.Count);
        while (m.PendingCells > 0)
        {
            Assert.True(m.TickIndex < guard, $"{what}: the decay never settled");
            RunTicks(m, tree.Chunks, d, 1);
        }
    }

    [Fact]
    public void OrphanCrumbleAgreesWithFelling()
    {
        var cases = new List<(string What, TreeSpecies Species, Func<StampedTree, List<(int X, int Y, int Z)>> Cuts, bool ExpectFall)>();

        var thin = StampedTree.WorldgenSpecies.First(s => StampedTree.PickLimb(
            TreeGrower.GrowToMaturity(s, RootX, RootY, RootZ, new VirtualTreeSpace((_, _) => RootY - 1), Seed)) >= 0);
        cases.Add(($"{thin.Name} limb", thin, t =>
        {
            var limb = StampedTree.PickLimb(t.Graph);
            return [(t.Graph[limb].X, t.Graph[limb].Y, t.Graph[limb].Z)];
        }, true));

        foreach (var wide in WideTreeSpecies.AllShapes)
        {
            cases.Add(($"{wide.Name} whole slice", wide, SliceCuts, true));
            cases.Add(($"{wide.Name} partial slice", wide, t => SliceCuts(t).SkipLast(1).ToList(), false));
        }

        foreach (var (what, species, cutsOf, expectFall) in cases)
        {
            var manager = new LeafDecayManager();
            var drops = new DropManager();

            var felledCopy = StampGrounded(species, manager);
            var expected = FelledBy(felledCopy, cutsOf(felledCopy));
            Assert.True(expectFall == expected.Count > 0,
                $"{what}: the fells dropped {expected.Count} wood cells besides the cuts");

            var tree = StampGrounded(species, manager);
            var cuts = cutsOf(tree);
            foreach (var cut in cuts)
                tree.Write(cut, 0, 0);
            var (min, max) = StampedTree.Bounds(tree.Wood.Keys.Concat(tree.Leaves.Keys));
            manager.ScheduleBox(min, max);
            RunUntilSettled(manager, tree, drops, what);

            var crumbled = tree.Wood.Keys.Where(c => !cuts.Contains(c) && tree.Read(c) != tree.Wood[c]).ToHashSet();
            Assert.True(expected.SetEquals(crumbled),
                $"{what}: the decay crumbled {crumbled.Count} wood cells, felling the same cuts drops {expected.Count}"
                + $" ({crumbled.Except(expected).Count()} extra, {expected.Except(crumbled).Count()} missing)");
            foreach (var cell in crumbled)
                Assert.True(IsAir(tree.Chunks, cell), $"{what}: crumbled wood at {cell} was rewritten, not removed");
        }
    }

    [Fact]
    public void ShellsOfAStandingWideTrunkNeverCrumble()
    {
        foreach (var species in WideTreeSpecies.AllShapes)
        {
            var manager = new LeafDecayManager();
            var drops = new DropManager();
            var tree = StampGrounded(species, manager);
            Assert.Contains(tree.Wood.Values, w => WoodState.IsShell(w.State));

            var (min, max) = StampedTree.Bounds(tree.Wood.Keys.Concat(tree.Leaves.Keys));
            manager.ScheduleBox(min, max);
            RunUntilSettled(manager, tree, drops, species.Name);

            foreach (var (cell, wood) in tree.Wood)
                Assert.True(wood == tree.Read(cell),
                    $"{species.Name}: standing wood at {cell} ({(WoodState.IsShell(wood.State) ? "shell" : "node")}) became {tree.Read(cell)}");
        }
    }
}
