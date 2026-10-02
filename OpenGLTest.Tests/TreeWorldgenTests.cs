using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Rules of the worldgen tree front-end (<see cref="TreeGenerator"/>): placement honours each
/// species' rules, the stamp is a pure function of position whatever order chunks generate in,
/// every stamped wood cell carries a consistent graph state, and wood only reaches below the
/// heightmap as the in-chunk root walk under a trunk. In the shape-cache collection because one
/// fact resets the tree caches between two generation passes.
/// </summary>
[Collection("Worldgen shape cache")]
public sealed class TreeWorldgenTests
{
    public TreeWorldgenTests() => TestContent.EnsureLoaded();

    private static int HeightAt(int x, int z) =>
        Chunk.GetColumnHeights(x >> Chunk.SizeShift, z >> Chunk.SizeShift)
            [((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask)];

    /// <summary>A chunk column whose 3x3 neighbourhood is forest and whose centre roots a tree.</summary>
    private static (int Cx, int Cz) FindForestTreeColumn()
    {
        for (var r = 1; r <= 48; r++)
        for (var cx = -r; cx <= r; cx++)
        for (var cz = -r; cz <= r; cz++)
        {
            if (Math.Max(Math.Abs(cx), Math.Abs(cz)) != r) continue;

            var allForest = true;
            for (var dx = -1; dx <= 1 && allForest; dx++)
            for (var dz = -1; dz <= 1 && allForest; dz++)
                allForest = TerrainGenerator.IsForest((cx + dx) * Chunk.Size + 8, (cz + dz) * Chunk.Size + 8);
            if (!allForest) continue;

            for (var x = 0; x < Chunk.Size; x++)
            for (var z = 0; z < Chunk.Size; z++)
                if (TreeGenerator.TryGetTree(cx * Chunk.Size + x, cz * Chunk.Size + z, out _, out _))
                    return (cx, cz);
        }

        throw new InvalidOperationException("no wooded forest chunk column within 48 chunks");
    }

    /// <summary>
    /// Generates the 3x3 chunk columns around (cx, cz) over the band from below the lowest surface
    /// to above the tallest tree, and returns every cell's (block, state) by world position.
    /// </summary>
    private static Dictionary<(int X, int Y, int Z), (byte Block, byte State)> Generate(int cx, int cz, bool reverse)
    {
        var minHeight = int.MaxValue;
        var maxHeight = int.MinValue;
        for (var dx = -1; dx <= 1; dx++)
        for (var dz = -1; dz <= 1; dz++)
        {
            var column = Chunk.GetColumnData(cx + dx, cz + dz);
            minHeight = Math.Min(minHeight, column.MinHeight);
            maxHeight = Math.Max(maxHeight, column.MaxHeight);
        }

        var cyLo = (minHeight >> Chunk.SizeShift) - 1;
        var cyHi = ((maxHeight + TreeGenerator.ReachUp) >> Chunk.SizeShift) + 1;
        var positions = new List<Vector3>();
        for (var dx = -1; dx <= 1; dx++)
        for (var dz = -1; dz <= 1; dz++)
        for (var cy = cyLo; cy <= cyHi; cy++)
            positions.Add(new Vector3(cx + dx, cy, cz + dz));
        if (reverse) positions.Reverse();

        var cells = new Dictionary<(int X, int Y, int Z), (byte Block, byte State)>();
        foreach (var position in positions)
        {
            var chunk = new Chunk(null!, position, generateBlockData: false);
            chunk.GenerateBlockData();
            var x0 = (int)position.X * Chunk.Size;
            var y0 = (int)position.Y * Chunk.Size;
            var z0 = (int)position.Z * Chunk.Size;
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
                cells[(x0 + x, y0 + y, z0 + z)] = (chunk.GetLocalBlock(x, y, z), chunk.GetLocalState(x, y, z));
        }

        return cells;
    }

    private static HashSet<byte> WoodIds() => TreeSpeciesRegistry.All.Select(s => s.WoodId).ToHashSet();

    private static HashSet<byte> LeafIds() =>
        TreeSpeciesRegistry.All.Where(s => s.HasLeaves).Select(s => s.LeavesId).ToHashSet();

    [Fact]
    public void StampIsIndependentOfGenerationOrder()
    {
        var (cx, cz) = FindForestTreeColumn();
        var forward = Generate(cx, cz, reverse: false);
        TreeGenerator.ResetCaches();
        var backward = Generate(cx, cz, reverse: true);

        var woodIds = WoodIds();
        Assert.Contains(forward.Values, c => woodIds.Contains(c.Block));
        Assert.Equal(forward.Count, backward.Count);
        foreach (var (cell, value) in forward)
            Assert.True(backward[cell] == value,
                $"{cell}: {value} forward, {backward[cell]} backward");
    }

    [Fact]
    public void EveryStampedWoodCellCarriesAGraphState()
    {
        var (cx, cz) = FindForestTreeColumn();
        var cells = Generate(cx, cz, reverse: false);
        var woodIds = WoodIds();
        var leafIds = LeafIds();

        var wood = 0;
        var leaves = 0;
        foreach (var ((x, y, z), (block, state)) in cells)
        {
            if (leafIds.Contains(block))
            {
                Assert.True(LeafState.Hydration(state) >= 1, $"leaf at ({x}, {y}, {z}) has no hydration");
                leaves++;
                continue;
            }

            if (!woodIds.Contains(block)) continue;
            wood++;
            Assert.False(WoodState.IsDetached(state), $"generated wood at ({x}, {y}, {z}) is detached");
            if (!WoodState.TryParentOffset(state, out var dx, out var dy, out var dz)) continue;
            if (!cells.TryGetValue((x + dx, y + dy, z + dz), out var parent)) continue;
            Assert.True(parent.Block == block,
                $"wood at ({x}, {y}, {z}) points at ({x + dx}, {y + dy}, {z + dz}), which holds {parent.Block}");
        }

        Assert.True(wood > 0, "no wood was stamped");
        Assert.True(leaves > 0, "no leaves were stamped");
    }

    [Fact]
    public void WoodNeverBuriesTerrain()
    {
        var (cx, cz) = FindForestTreeColumn();
        var cells = Generate(cx, cz, reverse: false);
        var woodIds = WoodIds();

        var wood = 0;
        foreach (var ((x, y, z), (block, state)) in cells)
        {
            if (!woodIds.Contains(block)) continue;
            wood++;
            if (y > HeightAt(x, z)) continue;

            // At or below the ground: only a root under a trunk, hanging from the base in one
            // contiguous column and inside the chunk that holds the base
            Assert.True(TreeGenerator.TryGetTree(x, z, out var species, out var baseY),
                $"wood at ({x}, {y}, {z}) is below the ground of a column that roots no tree");
            Assert.Equal(species.WoodId, block);
            Assert.True(y < baseY);
            for (var yy = y + 1; yy <= baseY; yy++)
                Assert.Equal(block, cells[(x, yy, z)].Block);
            Assert.Equal(TreeDirections.Up, WoodState.Parent(state));
            Assert.Equal(baseY >> Chunk.SizeShift, y >> Chunk.SizeShift);
        }

        Assert.True(wood > 0, "no wood was stamped");
    }

    [Fact]
    public void TryGetTreeHonoursItsSpeciesRules()
    {
        // The origin square (spawn clearing included) plus a desert and a taiga square, so every
        // shipped biome with trees is sampled
        var areas = new List<(int X0, int Z0)> { (-48, -48) };
        foreach (var biome in new[] { Biome.Desert, Biome.Taiga })
        {
            var found = false;
            for (var cx = -64; cx <= 64 && !found; cx++)
            for (var cz = -64; cz <= 64 && !found; cz++)
            {
                if (TerrainGenerator.BiomeAt(cx * Chunk.Size + 8, cz * Chunk.Size + 8) != biome) continue;
                areas.Add((cx * Chunk.Size, cz * Chunk.Size));
                found = true;
            }
        }

        var species = new HashSet<string>();
        foreach (var (x0, z0) in areas)
        for (var x = x0; x < x0 + 96; x++)
        for (var z = z0; z < z0 + 96; z++)
        {
            if (!TreeGenerator.TryGetTree(x, z, out var tree, out var baseY)) continue;
            species.Add(tree.Name);

            var rules = tree.Worldgen!;
            var column = Chunk.GetColumnData(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
            var li = ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);
            var height = column.Heights[li];

            Assert.True(rules.GrowsIn(TerrainGenerator.BiomeAt(x, z)), $"{tree.Name} outside its biomes at ({x}, {z})");
            Assert.False(TerrainGenerator.IsWaterColumn(x, z, height), $"{tree.Name} in water at ({x}, {z})");
            Assert.True(column.DirtDepth[li] >= rules.MinSoil);
            Assert.InRange(height, rules.MinGround, rules.MaxGround);
            Assert.Equal(height + 1, baseY);
            if (rules.SpawnClear)
                Assert.False(Math.Abs(x) <= WorldGenerationConfig.TreeSpawnClearRadius
                             && Math.Abs(z) <= WorldGenerationConfig.TreeSpawnClearRadius,
                    $"{tree.Name} roots inside the spawn clearing at ({x}, {z})");

            Assert.True(TreeGenerator.TryGetTree(x, z, out var again, out var againY));
            Assert.Same(tree, again);
            Assert.Equal(baseY, againY);
        }

        Assert.True(species.Count >= 2, $"only {string.Join(", ", species)} sampled");
    }
}
