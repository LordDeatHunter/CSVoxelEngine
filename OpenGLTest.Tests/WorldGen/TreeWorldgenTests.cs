using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using VoxelEngine.WorldGen.Trees;
using Xunit;

namespace VoxelEngine.Tests.WorldGen;

/// <summary>
/// Rules of the worldgen tree front-end (<see cref="TreeGenerator"/>): placement honors each
/// species' rules, the stamp is a pure function of position whatever order chunks generate in,
/// every stamped wood cell carries a consistent graph state, and wood only reaches below the
/// heightmap as the in-chunk root walk under a trunk. In the shape-cache collection because one
/// fact resets the tree caches between two generation passes.
/// </summary>
[Collection("Worldgen shape cache")]
public sealed class TreeWorldgenTests
{
    public TreeWorldgenTests() => TestContent.EnsureLoaded();

    [Fact]
    public void StampIsIndependentOfGenerationOrder()
    {
        var (cx, cz) = TreeWorldgenFixture.FindForestTreeColumn();
        var forward = TreeWorldgenFixture.Generate(cx, cz, reverse: false);
        TreeGenerator.ResetCaches();
        var backward = TreeWorldgenFixture.Generate(cx, cz, reverse: true);

        var woodIds = TreeWorldgenFixture.WoodIds();
        Assert.Contains(forward.Values, c => woodIds.Contains(c.Block));
        Assert.Equal(forward.Count, backward.Count);
        foreach (var (cell, value) in forward)
            Assert.True(backward[cell] == value,
                $"{cell}: {value} forward, {backward[cell]} backward");
    }

    [Fact]
    public void EveryStampedWoodCellCarriesAGraphState()
    {
        var (cx, cz) = TreeWorldgenFixture.FindForestTreeColumn();
        var cells = TreeWorldgenFixture.Generate(cx, cz, reverse: false);
        var woodIds = TreeWorldgenFixture.WoodIds();
        var leafIds = TreeWorldgenFixture.LeafIds();

        var wood = 0;
        var leaves = 0;
        var logs = 0;
        foreach (var ((x, y, z), (block, state)) in cells)
        {
            if (leafIds.Contains(block))
            {
                Assert.True(LeafState.Hydration(state) >= 1, $"leaf at ({x}, {y}, {z}) has no hydration");
                leaves++;
                continue;
            }

            if (!woodIds.Contains(block)) continue;
            // Detached wood is the scenery generator's fallen log, not a tree node
            if (WoodState.IsDetached(state))
            {
                Assert.True(IsFallenLogCell(x, y, z), $"detached wood at ({x}, {y}, {z}) is not a fallen log");
                logs++;
                continue;
            }

            wood++;
            Assert.False(WoodState.IsDetached(state), $"generated wood at ({x}, {y}, {z}) is detached");
            if (!WoodState.TryParentOffset(state, out var dx, out var dy, out var dz)) continue;
            if (!cells.TryGetValue((x + dx, y + dy, z + dz), out var parent)) continue;
            Assert.True(parent.Block == block,
                $"wood at ({x}, {y}, {z}) points at ({x + dx}, {y + dy}, {z + dz}), which holds {parent.Block}");
        }

        Assert.True(wood > 0, "no wood was stamped");
        Assert.True(leaves > 0, "no leaves were stamped");
        _ = logs;
    }

    // Whether a detached wood cell belongs to a fallen log: some hashed log starts within its own
    // length behind the cell on its own axis and lies at its start column's ground plus one
    private static bool IsFallenLogCell(int x, int y, int z)
    {
        var shift = WorldGenerationConfig.FallenLogCellShift;
        for (var back = 0; back < WorldGenerationConfig.FallenLogMaxLength; back++)
        for (var axis = 0; axis < 2; axis++)
        {
            var sx = axis == 0 ? x - back : x;
            var sz = axis == 0 ? z : z - back;
            if (!FallenLogGenerator.TryGetLog(sx >> shift, sz >> shift, out var startX, out var startZ,
                    out var length, out var alongX))
                continue;
            if (startX != sx || startZ != sz || back >= length) continue;
            if (alongX != (axis == 0)) continue;
            if (y != TreeWorldgenFixture.HeightAt(startX, startZ) + 1) continue;
            return true;
        }
        return false;
    }

    [Fact]
    public void WoodNeverBuriesTerrain()
    {
        var (cx, cz) = TreeWorldgenFixture.FindForestTreeColumn();
        var cells = TreeWorldgenFixture.Generate(cx, cz, reverse: false);
        var woodIds = TreeWorldgenFixture.WoodIds();

        var wood = 0;
        foreach (var ((x, y, z), (block, state)) in cells)
        {
            if (!woodIds.Contains(block)) continue;
            wood++;
            if (y > TreeWorldgenFixture.HeightAt(x, z)) continue;

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
            var column = ColumnCache.Get(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
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
