using System;
using System.Collections.Generic;
using VoxelEngine.Chunks;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Shape;

namespace VoxelEngine.WorldGen.Hell;

/// <summary>
/// The hell pockets' trees (plans/LAYERS.md L-9): the fungal tree of the Fungal Grove and the
/// charred tree of the Ashlands (<see cref="HellBiomePalette.Tree"/>). Hell has no heightmap, so they
/// do not go through <see cref="TreeGenerator"/>: hell is cut into square cells of
/// <see cref="WorldGenerationConfig.HellTreeCellBlocks"/> columns, and each cell of each layer roots
/// at most one tree, a pure function of (cell, layer, seed). A hash picks the root column (at least
/// <see cref="WorldGenerationConfig.HellTreeCellMargin"/> inside the cell), the column's biome picks
/// the species and its chance, and a hash picks one of the column's floors in the layer's pocket;
/// the root must stand on the biome's surface. The tree then grows to maturity in a
/// <see cref="HellTreeSpace"/>: the pocket's air (before any flora) inside its own cell only, so two
/// trees never meet and a tree never enters rock. The grown shape is cached
/// (<see cref="GenerationalCache{TKey, TValue}"/>, a pure function of its key) and every chunk it
/// crosses stamps the part inside itself, so a tree is the same whichever chunk stamps it.
/// </summary>
internal static class HellTrees
{
    private const int CellBlocks = WorldGenerationConfig.HellTreeCellBlocks;
    private const int Margin = WorldGenerationConfig.HellTreeCellMargin;
    private const int RootSpan = CellBlocks - 2 * Margin;
    private const int Period = WorldGenerationConfig.HellLayerPeriodBlocks;

    private static readonly GenerationalCache<(int X, int Z, int Layer), TreeBatches.TreeShape?> Cache =
        new(WorldGenerationConfig.HellTreeCacheCapacity, WorldGenerationConfig.HellTreeCacheGenerations);

    // A static field, not a lambda at the call site: a closure would allocate on every call (see
    // TreeBatches.BuildShapeFor)
    private static readonly Func<(int X, int Z, int Layer), TreeBatches.TreeShape?> BuildFor =
        static key => Build(key.X, key.Z, key.Layer);

    static HellTrees()
    {
        if (Chunk.Size % CellBlocks != 0)
            throw new InvalidOperationException($"HellTreeCellBlocks ({CellBlocks}) must divide the chunk size.");
        if (RootSpan < 1)
            throw new InvalidOperationException("HellTreeCellMargin leaves no root column in a hell tree cell.");
    }

    /// <summary>Drops every cached tree (<see cref="WorldSeed.Apply"/>): they depend on the seed.</summary>
    internal static void ResetCache() => Cache.Clear();

    /// <summary>Which tree cell world column coordinate <paramref name="v"/> lies in.</summary>
    internal static int CellOf(int v) => (int)Math.Floor((double)v / CellBlocks);

    /// <summary>The tree rooted in cell (cellX, cellZ) of hell layer <paramref name="layer"/>, or null.</summary>
    internal static TreeBatches.TreeShape? TreeOf(int cellX, int cellZ, int layer) =>
        Cache.GetOrAdd((cellX, cellZ, layer), BuildFor);

    /// <summary>
    /// Writes the part of every hell tree inside the chunk whose minimum corner is (worldX0, worldY0,
    /// worldZ0) into <paramref name="blocks"/>, with each cell's state byte into
    /// <paramref name="state"/> (allocated on the first write). Runs after <see cref="HellGenerator.Stamp"/>
    /// and overwrites whatever flora stands in a tree cell; every tree cell is pocket air before the
    /// flora, so nothing else is ever overwritten. A tree stays inside its cell's columns and its
    /// layer's pocket, so only the chunk's own cells and layers are read.
    /// </summary>
    internal static void Stamp(byte[] blocks, ref byte[]? state, int worldX0, int worldY0, int worldZ0)
    {
        var yTop = Math.Min(worldY0 + Chunk.Size - 1, WorldGenerationConfig.HellTopY);
        if (yTop < worldY0) return;

        var anyOpen = false;
        for (var y = worldY0; y <= yTop && !anyOpen; y++)
            anyOpen = HellGenerator.RowCanOpen(y);
        if (!anyOpen) return;

        for (var layer = HellGenerator.LayerIndex(yTop); layer <= HellGenerator.LayerIndex(worldY0); layer++)
        for (var cx = CellOf(worldX0); cx <= CellOf(worldX0 + Chunk.Size - 1); cx++)
        for (var cz = CellOf(worldZ0); cz <= CellOf(worldZ0 + Chunk.Size - 1); cz++)
        {
            var tree = TreeOf(cx, cz, layer);
            if (tree == null || tree.MaxY < worldY0 || tree.MinY > yTop) continue;

            foreach (var cell in tree.Cells)
            {
                var y = cell.Y - worldY0;
                if (y < 0 || y >= Chunk.Size) continue;
                var index = Chunk.Idx(cell.X - worldX0, y, cell.Z - worldZ0);
                blocks[index] = cell.Block;
                state ??= new byte[Chunk.BlockCount];
                state[index] = cell.State;
            }
        }
    }

    /// <summary>The cache's factory: the tree of one cell and layer, or null.</summary>
    private static TreeBatches.TreeShape? Build(int cellX, int cellZ, int layer)
    {
        var hash = SplitMix64.Hash(cellX, cellZ, WorldGenerationConfig.Seed ^ (layer * 0x2545F491));
        var x0 = cellX * CellBlocks;
        var z0 = cellZ * CellBlocks;
        var x = x0 + Margin + (int)(SplitMix64.Unit(hash, 0) * RootSpan);
        var z = z0 + Margin + (int)(SplitMix64.Unit(hash, 1) * RootSpan);

        var palette = HellBiomes.Palette(HellBiomes.AtLayer(x, layer, z));
        if (palette.Tree is not { } species || SplitMix64.Unit(hash, 2) >= palette.TreeChance) return null;

        var samples = new Dictionary<long, float>();
        var baseY = PickFloor(x, z, layer, SplitMix64.Unit(hash, 3), samples);
        if (baseY == int.MinValue || HellGenerator.BlockAt(x, baseY - 1, z) != palette.Surface) return null;

        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var space = new HellTreeSpace(x0, z0, CellBlocks, samples);
        var graph = TreeGrower.GrowToMaturity(species, x, baseY, z, space);
        var cells = TreeRasterizer.Rasterize(graph, species, space, out var woodCount).ToArray();
        WorldgenStats.AddGrow(System.Diagnostics.Stopwatch.GetTimestamp() - start);
        return TreeBatches.TreeShape.Build(species, x, baseY, z, cells, woodCount);
    }

    /// <summary>One floor of column (x, z) in <paramref name="layer"/>'s pocket, picked by
    /// <paramref name="pick"/> (0..1) among them all: the lowest air cell of an air run whose cell
    /// below is not air. <see cref="int.MinValue"/> when the column has none.</summary>
    private static int PickFloor(int x, int z, int layer, float pick, Dictionary<long, float> samples)
    {
        var top = WorldGenerationConfig.HellTopY - layer * Period;
        var bottom = top - HellGenerator.PocketHeight + 1;

        Span<int> floors = stackalloc int[HellGenerator.PocketHeight];
        var count = 0;
        var aboveIsAir = false;
        for (var y = top; y >= bottom - 1; y--)
        {
            var air = y >= bottom && HellGenerator.IsCavernAir(x, y, z, samples);
            if (aboveIsAir && !air) floors[count++] = y + 1;
            aboveIsAir = air;
        }

        return count == 0 ? int.MinValue : floors[Math.Min((int)(pick * count), count - 1)];
    }
}
