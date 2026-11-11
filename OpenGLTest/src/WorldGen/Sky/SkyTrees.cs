using System;
using VoxelEngine.Chunks;
using VoxelEngine.WorldGen.Features;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Shape;

namespace VoxelEngine.WorldGen.Sky;

/// <summary>
/// The sky islands' trees (plans/LAYERS.md <c>L-13</c>): the broad sky oak of the meadow and the glowing
/// crystal tree of the frost highland (<see cref="SkyIslandPalette.Tree"/>). The sky has no heightmap, so
/// they do not go through <see cref="TreeGenerator"/>: the sky is cut into square cells of
/// <see cref="SkyIslandConfig.SkyTreeCellBlocks"/> columns, and each cell roots at most one tree, a pure
/// function of (cell, seed). A hash picks the root column (at least <see cref="SkyIslandConfig.SkyTreeCellMargin"/>
/// inside the cell); it must be an island column whose top cell is its biome's plain top (not a sparkle), the
/// island's biome picks the species and its chance, and the tree roots in the cell over that top. It grows to
/// maturity in a <see cref="SkyTreeSpace"/>: the band's open sky inside its own cell's columns only, so two
/// trees never meet, a tree never enters an island and never leaves the band. The grown shape is cached
/// (<see cref="GenerationalCache{TKey, TValue}"/>, a pure function of its key) and every chunk it crosses
/// stamps the part inside itself, so a tree is the same whichever chunk stamps it.
/// </summary>
internal static class SkyTrees
{
    private const int CellBlocks = SkyIslandConfig.SkyTreeCellBlocks;
    private const int Margin = SkyIslandConfig.SkyTreeCellMargin;
    private const int RootSpan = CellBlocks - 2 * Margin;
    private const int Salt = 0x534B5452; // "SKTR"

    private static readonly GenerationalCache<(int X, int Z), TreeBatches.TreeShape?> Cache =
        new(WorldGenerationConfig.SkyTreeCacheCapacity, WorldGenerationConfig.SkyTreeCacheGenerations);

    // A static field, not a lambda at the call site: a closure would allocate on every call (see
    // TreeBatches.BuildShapeFor)
    private static readonly Func<(int X, int Z), TreeBatches.TreeShape?> BuildFor = static key => Build(key.X, key.Z);

    static SkyTrees()
    {
        if (Chunk.Size % CellBlocks != 0)
            throw new InvalidOperationException($"SkyTreeCellBlocks ({CellBlocks}) must divide the chunk size.");
        if (RootSpan < 1)
            throw new InvalidOperationException("SkyTreeCellMargin leaves no root column in a sky tree cell.");
    }

    /// <summary>Drops every cached tree (<see cref="WorldSeed.Apply"/>): they depend on the seed.</summary>
    internal static void ResetCache() => Cache.Clear();

    /// <summary>Which tree cell world column coordinate <paramref name="v"/> lies in.</summary>
    internal static int CellOf(int v) => (int)Math.Floor((double)v / CellBlocks);

    /// <summary>The tree rooted in cell (cellX, cellZ), or null.</summary>
    internal static TreeBatches.TreeShape? TreeOf(int cellX, int cellZ) => Cache.GetOrAdd((cellX, cellZ), BuildFor);

    /// <summary>
    /// Writes the part of every sky tree inside the chunk whose minimum corner is (worldX0, worldY0, worldZ0)
    /// into <paramref name="blocks"/>, with each cell's state byte into <paramref name="state"/> (allocated on
    /// the first write). Runs after <see cref="SkyIslandGenerator.Stamp"/>; a tree cell is open sky before the
    /// flora, so it only ever writes over air or a plant or vine, and leaves anything solid alone.
    /// </summary>
    internal static void Stamp(ushort[] blocks, ref byte[]? state, int worldX0, int worldY0, int worldZ0)
    {
        if (!WriteBand().Reaches(worldY0)) return;
        // A chunk no island reaches holds no tree root
        if (SkyIslands.CollectIslands(worldX0, worldZ0, worldX0 + Chunk.Size - 1, worldZ0 + Chunk.Size - 1,
                stackalloc SkyIsland[SkyIslands.MaxIslandsPerBox]) == 0)
            return;

        var yTop = worldY0 + Chunk.Size - 1;
        for (var cx = CellOf(worldX0); cx <= CellOf(worldX0 + Chunk.Size - 1); cx++)
        for (var cz = CellOf(worldZ0); cz <= CellOf(worldZ0 + Chunk.Size - 1); cz++)
        {
            var tree = TreeOf(cx, cz);
            if (tree == null || tree.MaxY < worldY0 || tree.MinY > yTop) continue;

            foreach (var cell in tree.Cells)
            {
                var y = cell.Y - worldY0;
                if (y < 0 || y >= Chunk.Size) continue;
                var index = Chunk.Idx(cell.X - worldX0, y, cell.Z - worldZ0);
                if (BlockType.IsSolid(blocks[index])) continue;
                blocks[index] = cell.Block;
                state ??= new byte[Chunk.BlockCount];
                state[index] = cell.State;
            }
        }
    }

    /// <summary>Every world y <see cref="Stamp"/> can write: the island band (the tree space never leaves it).</summary>
    internal static StampBand WriteBand() => SkyIslandGenerator.WriteBand();

    /// <summary>The cache's factory: the tree of one cell, or null.</summary>
    private static TreeBatches.TreeShape? Build(int cellX, int cellZ)
    {
        var hash = SplitMix64.Hash(cellX, cellZ, WorldGenerationConfig.Seed ^ Salt);
        var x0 = cellX * CellBlocks;
        var z0 = cellZ * CellBlocks;
        var x = x0 + Margin + (int)(SplitMix64.Unit(hash, 0) * RootSpan);
        var z = z0 + Margin + (int)(SplitMix64.Unit(hash, 1) * RootSpan);

        if (!SkyIslandGenerator.TryGetColumn(x, z, out var island, out _, out var top)) return null;
        var palette = SkyBiomes.Palette(SkyBiomes.OfColumn(island, x, z));
        if (palette.Tree is not { } species || SplitMix64.Unit(hash, 2) >= palette.TreeChance) return null;
        if (SkyFlora.IsSparkle(palette, x, z)) return null;

        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var space = new SkyTreeSpace(x0, z0, CellBlocks);
        var graph = TreeGrower.GrowToMaturity(species, x, top + 1, z, space);
        var cells = TreeRasterizer.Rasterize(graph, species, space, out var woodCount).ToArray();
        WorldgenStats.AddGrow(System.Diagnostics.Stopwatch.GetTimestamp() - start);
        return TreeBatches.TreeShape.Build(species, x, top + 1, z, cells, woodCount);
    }
}
