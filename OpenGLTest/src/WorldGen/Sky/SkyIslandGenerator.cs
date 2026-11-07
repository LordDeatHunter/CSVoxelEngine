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
/// built of a <see cref="SkyIslandPalette"/>. A pure function of (x, z) and the seed: a chunk collects the
/// few islands that reach it once and asks <see cref="TryGetColumn(in SkyIsland, int, int, out int, out int)"/>
/// per column, the very rule <see cref="BlockAt"/> asks, so the point query equals the stamp bit for bit
/// across chunk seams, and a chunk reads no neighbor. Islands never touch, so at most one holds a column.
/// It only ever replaces air.
/// </summary>
public static class SkyIslandGenerator
{
    // At most 3 x 3 island cells reach a 16-wide box (see CollectIslands); room to spare
    private const int MaxIslandsPerChunk = 16;

    /// <summary>
    /// The cells of <paramref name="island"/> in column (x, z), inclusive world y: false outside its
    /// outline. The top is at or above the island's base and the bottom below it, so every column is at
    /// least two cells thick.
    /// </summary>
    public static bool TryGetColumn(in SkyIsland island, int x, int z, out int bottom, out int top)
    {
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
    public static bool TryGetColumn(int x, int z, out int bottom, out int top)
    {
        Span<SkyIsland> islands = stackalloc SkyIsland[MaxIslandsPerChunk];
        var count = SkyIslands.CollectIslands(x, z, x, z, islands);
        return TryGetColumn(islands[..count], x, z, out bottom, out top);
    }

    /// <summary>The island block at the cell, or air: on air this is what <see cref="Stamp"/> writes.</summary>
    public static byte BlockAt(int x, int y, int z) =>
        TryGetColumn(x, z, out var bottom, out var top) && y >= bottom && y <= top
            ? SkyIslandPalette.Default.BlockAt(y, top)
            : BlockType.Air;

    /// <summary>Writes the islands into the air cells of the chunk whose minimum corner is
    /// (worldX0, worldY0, worldZ0).</summary>
    public static void Stamp(byte[] blocks, int worldX0, int worldY0, int worldZ0)
    {
        var yMin = Math.Max(0, WorldGenerationConfig.SkyIslandBottomY - worldY0);
        var yMax = Math.Min(Chunk.Size - 1, WorldGenerationConfig.SkyIslandTopY - worldY0);
        if (yMin > yMax) return;

        Span<SkyIsland> islands = stackalloc SkyIsland[MaxIslandsPerChunk];
        var count = SkyIslands.CollectIslands(worldX0, worldZ0, worldX0 + Chunk.Size - 1, worldZ0 + Chunk.Size - 1, islands);
        if (count == 0) return;

        // L-13's sky biomes pick the palette here, per island or per column
        var palette = SkyIslandPalette.Default;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            if (!TryGetColumn(islands[..count], worldX0 + x, worldZ0 + z, out var bottom, out var top)) continue;

            var lo = Math.Max(yMin, bottom - worldY0);
            var hi = Math.Min(yMax, top - worldY0);
            for (var y = lo; y <= hi; y++)
            {
                var i = Chunk.Idx(x, y, z);
                if (blocks[i] == BlockType.Air)
                    blocks[i] = palette.BlockAt(worldY0 + y, top);
            }
        }
    }

    /// <summary>Every world y <see cref="Stamp"/> can write: the island band.</summary>
    internal static StampBand WriteBand() =>
        new(WorldGenerationConfig.SkyIslandBottomY, WorldGenerationConfig.SkyIslandTopY);

    /// <summary>The column of the first island in <paramref name="islands"/> that holds (x, z).</summary>
    private static bool TryGetColumn(ReadOnlySpan<SkyIsland> islands, int x, int z, out int bottom, out int top)
    {
        foreach (ref readonly var island in islands)
            if (TryGetColumn(island, x, z, out bottom, out top))
                return true;

        bottom = 0;
        top = -1;
        return false;
    }
}
