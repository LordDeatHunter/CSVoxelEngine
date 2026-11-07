using System;
using VoxelEngine.Chunks;
using VoxelEngine.WorldGen.Features;

namespace VoxelEngine.WorldGen.Sky;

/// <summary>
/// The floating sky islands' shape and stamp (plans/LAYERS.md <c>L-12</c>), over the sites
/// <see cref="SkyIslands"/> places: each island is a blob whose outline wobbles inward of its nominal disc,
/// whose top domes gently up from its base (highest at the center, roughened by a fine noise) and whose
/// underside hangs below the base, deepest under the center and tapering to a point at the rim
/// ((1 - t)^<see cref="SkyIslandConfig.IslandTaperExponent"/>), with stalactites dripping from it. It is
/// built of its sky biome's <see cref="SkyIslandPalette"/> (<see cref="SkyBiomes.OfIsland"/>), with that
/// biome's sparkle, plants and vines (<see cref="SkyFlora"/>). A pure function of (x, z) and the seed: a chunk collects the
/// few islands that reach it once and asks <see cref="TryGetColumn(in SkyIsland, int, int, out int, out int)"/>
/// per column, the very rule <see cref="BlockAt"/> asks, so the point query equals the stamp bit for bit
/// across chunk seams, and a chunk reads no neighbor. Islands never touch, so at most one holds a column.
/// It only ever replaces air; the vines hang below an island and its plants stand on it, still inside the
/// band, which the island's height roll leaves room for.
/// </summary>
public static class SkyIslandGenerator
{
    /// <summary>
    /// The cells of <paramref name="island"/> in column (x, z), inclusive world y: false outside its
    /// outline. The top is at or above the island's base and the bottom below it, so every column is at
    /// least two cells thick.
    /// </summary>
    public static bool TryGetColumn(in SkyIsland island, int x, int z, out int bottom, out int top)
    {
        if (island.IsLandmass) return SkyLandmasses.TryGetColumn(island, x, z, out bottom, out top);

        bottom = 0;
        top = -1;
        var dx = x - island.CenterX;
        var dz = z - island.CenterZ;
        var d2 = dx * dx + dz * dz;
        if (d2 >= island.Radius * island.Radius) return false;

        var outline = TerrainSites.InwardWobble(island.Radius, SkyIslandConfig.IslandWobble,
            SkyIslands.OutlineNoise, x, z);
        var d = MathF.Sqrt(d2);
        if (d >= outline) return false;

        var t = d / outline;
        var detail = 0.5f * (SkyIslands.TopNoise.GetNoise(x, z) + 1f);
        top = island.BaseY + (int)(island.Dome * (1f - t * t) + SkyIslandConfig.IslandTopDetail * detail * (1f - t) + 0.5f);

        var stalactite = SkyIslands.StalactiteNoise.GetNoise(x, z);
        var drip = stalactite > SkyIslandConfig.IslandStalactiteThreshold
            ? (stalactite - SkyIslandConfig.IslandStalactiteThreshold) / (1f - SkyIslandConfig.IslandStalactiteThreshold)
              * SkyIslandConfig.IslandStalactiteMax * MathF.Sqrt(1f - t)
            : 0f;
        bottom = island.BaseY - 1 - (int)(island.Depth * MathF.Pow(1f - t, SkyIslandConfig.IslandTaperExponent) + drip);
        return true;
    }

    /// <summary>The island cells of column (x, z): false in open sky. Collects the column's islands itself, so
    /// it is a plain point query, slower than a chunk's stamp.</summary>
    public static bool TryGetColumn(int x, int z, out int bottom, out int top) =>
        TryGetColumn(x, z, out _, out bottom, out top);

    /// <summary>The island holding column (x, z) and its cells: false in open sky.</summary>
    public static bool TryGetColumn(int x, int z, out SkyIsland island, out int bottom, out int top)
    {
        Span<SkyIsland> islands = stackalloc SkyIsland[SkyIslands.MaxIslandsPerBox];
        var count = SkyIslands.CollectIslands(x, z, x, z, islands);
        return TryGetColumn(islands[..count], x, z, out island, out bottom, out top);
    }

    /// <summary>The island block at the cell, with its biome's sparkle, plant and vine, or air: on air this
    /// is what <see cref="Stamp"/> writes. Trees are separate (<see cref="SkyTrees"/>).</summary>
    public static byte BlockAt(int x, int y, int z)
    {
        if (!TryGetColumn(x, z, out var island, out var bottom, out var top)) return BlockType.Air;
        return SkyFlora.BlockAt(SkyBiomes.Palette(SkyBiomes.OfColumn(island, x, z)), x, y, z, bottom, top);
    }

    /// <summary>Writes the islands into the air cells of the chunk whose minimum corner is
    /// (worldX0, worldY0, worldZ0).</summary>
    public static void Stamp(byte[] blocks, int worldX0, int worldY0, int worldZ0)
    {
        var yMin = Math.Max(0, WorldGenerationConfig.SkyIslandBottomY - worldY0);
        var yMax = Math.Min(Chunk.Size - 1, WorldGenerationConfig.SkyIslandTopY - worldY0);
        if (yMin > yMax) return;

        Span<SkyIsland> islands = stackalloc SkyIsland[SkyIslands.MaxIslandsPerBox];
        var count = SkyIslands.CollectIslands(worldX0, worldZ0, worldX0 + Chunk.Size - 1, worldZ0 + Chunk.Size - 1, islands);
        if (count == 0) return;

        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var wx = worldX0 + x;
            var wz = worldZ0 + z;
            var k = IndexOfColumn(islands[..count], wx, wz, out var bottom, out var top);
            if (k < 0) continue;

            // The column's cells, the plant over its top and the longest vine under it
            var palette = SkyBiomes.Palette(SkyBiomes.OfColumn(islands[k], wx, wz));
            var lo = Math.Max(yMin, bottom - SkyIslandConfig.SkyVineMaxLength - worldY0);
            var hi = Math.Min(yMax, top + 1 - worldY0);
            for (var y = lo; y <= hi; y++)
            {
                var i = Chunk.Idx(x, y, z);
                if (blocks[i] != BlockType.Air) continue;
                var block = SkyFlora.BlockAt(palette, wx, worldY0 + y, wz, bottom, top);
                if (block != BlockType.Air) blocks[i] = block;
            }
        }
    }

    /// <summary>Every world y <see cref="Stamp"/> can write: the island band.</summary>
    internal static StampBand WriteBand() =>
        new(WorldGenerationConfig.SkyIslandBottomY, WorldGenerationConfig.SkyIslandTopY);

    /// <summary>The first island in <paramref name="islands"/> that holds (x, z), and its column.</summary>
    private static bool TryGetColumn(ReadOnlySpan<SkyIsland> islands, int x, int z, out SkyIsland island,
        out int bottom, out int top)
    {
        var k = IndexOfColumn(islands, x, z, out bottom, out top);
        island = k < 0 ? default : islands[k];
        return k >= 0;
    }

    /// <summary>The index of the first island in <paramref name="islands"/> that holds (x, z), and its
    /// column; -1 when none does.</summary>
    private static int IndexOfColumn(ReadOnlySpan<SkyIsland> islands, int x, int z, out int bottom, out int top)
    {
        for (var k = 0; k < islands.Length; k++)
            if (TryGetColumn(islands[k], x, z, out bottom, out top))
                return k;

        bottom = 0;
        top = -1;
        return -1;
    }
}
