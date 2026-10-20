using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using VoxelEngine.Chunks;
using VoxelEngine.Lod;
using VoxelEngine.WorldGen.Trees.Shape;

namespace VoxelEngine.WorldGen;

/// <summary>
/// The far-LOD canopy over the tree worldgen: the grown canopy tops per chunk column (the batch
/// canopy the first LOD ring reads), the species' crown proxies the farther rings read, and the
/// felled-canopy markers player edits leave (<see cref="PoisonCanopy"/>), which the per-world LOD
/// edit save carries. Lookups are safe from any thread; the markers are written on the main
/// thread. <see cref="TreeGenerator"/> forwards its public canopy queries here.
/// </summary>
internal static class TreeCanopy
{
    // Canopy tops per chunk column (index (x & mask) << shift | (z & mask)): the highest leaf
    // cell over each ground column, 0 where the sky is clear, plus the leaf block that owns
    // that top cell. The far LOD reads both instead of block data so distant forests show in the
    // right foliage. Entries are a pure function of the chunk column (BuildCanopyTops reads only
    // the cell batches, themselves a pure function of their cell), so eviction is always safe --
    // edits never enter here, they live in CanopyDisturbed, which the CALLERS consult.
    internal sealed class CanopyColumns(int[] tops, byte[] leafBlocks)
    {
        public readonly int[] Tops = tops;
        public readonly byte[] LeafBlocks = leafBlocks;
    }

    // Its one engine caller is the far LOD: LodRegion.BuildMeshData asks TryGetCanopy once per
    // FINE column of a region, so a ring sweep walks far more distinct chunk columns than the cap
    // holds. Sizing knobs live in WorldGenerationConfig; the safety argument lives in
    // GenerationalCache.
    private static readonly GenerationalCache<(int X, int Z), CanopyColumns> CanopyCache =
        new(WorldGenerationConfig.TreeCanopyCacheCapacity, WorldGenerationConfig.TreeCanopyCacheGenerations);

    // Static delegate, not a lambda at the call site: a closure would allocate on every call, hits
    // included, on the LOD builder thread (see BuildShapeFor).
    private static readonly Func<(int X, int Z), CanopyColumns> BuildCanopyFor =
        static key => BuildCanopyTops(key.X, key.Z);

    // The far rings' canopy (LodLevelSpec.ProxyCanopy): the species' lod crown proxies per chunk
    // column, laid out like CanopyCache's entries. Cheap to rebuild and a pure function of the
    // chunk column, read by the same per-fine-column sweep, so it is sized like the real one.
    private static readonly GenerationalCache<(int X, int Z), CanopyColumns> ProxyCanopyCache =
        new(WorldGenerationConfig.TreeProxyCanopyCacheCapacity, WorldGenerationConfig.TreeProxyCanopyCacheGenerations);

    // Static delegate, not a lambda at the call site (see BuildShapeFor).
    private static readonly Func<(int X, int Z), CanopyColumns> BuildProxyCanopyFor =
        static key => BuildProxyCanopyTops(key.X, key.Z);

    // Fine columns whose canopy was edited by the player (mined wood/leaves, blasts, fells).
    // The static canopy above knows nothing of edits, so lookups here read 0 (no bump) rather
    // than resurrecting a chopped crown at long range. Entries are gameplay truth, so the cap
    // only bounds memory; clearing it can briefly show a stale bump far away, never a hole.
    // Process-global but per WORLD: WorldSeed.Apply resets it between worlds (ResetCanopyDisturbed),
    // and a save carries it across a restart through LodEditState (lod-edits.bin; the wholesale
    // Clear at CanopyDisturbedLimit makes that lossy in a very long session, M-10).
    private static readonly ConcurrentDictionary<(int X, int Z), byte> CanopyDisturbed = new();

    /// <summary>Drops the cached canopy tops (real and proxy); called by <see cref="TreeGenerator.ResetCaches"/> only.</summary>
    internal static void ResetCaches()
    {
        CanopyCache.Clear();
        ProxyCanopyCache.Clear();
    }

    /// <summary>
    /// Forgets every felled-canopy marker, which belongs to the previous world's edits; only
    /// <see cref="WorldSeed.Apply"/> calls it.
    /// </summary>
    internal static void ResetCanopyDisturbed() => CanopyDisturbed.Clear();

    /// <summary>
    /// Highest canopy cell over the ground column (x, z), or 0 under open sky. Disturbed
    /// columns (see <see cref="PoisonCanopy"/>) read 0. Safe to call from any thread.
    /// </summary>
    public static int CanopyTop(int x, int z)
    {
        if (CanopyDisturbed.ContainsKey((x, z)))
            return 0;

        return GetCanopyTops(x >> Chunk.SizeShift, z >> Chunk.SizeShift)
            [((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask)];
    }

    public static byte CanopyLeafBlock(int x, int z)
    {
        if (CanopyDisturbed.ContainsKey((x, z)))
            return BlockType.Air;

        TryGetCanopy(x, z, out _, out var leaf);
        return leaf;
    }

    /// <summary>The grown canopy over (x, z); see <see cref="TryGetCanopy(int, int, bool, out int, out byte)"/>.</summary>
    public static bool TryGetCanopy(int x, int z, out int top, out byte leaf) =>
        TryGetCanopy(x, z, proxy: false, out top, out leaf);

    /// <summary>
    /// The canopy top over the ground column (x, z) and the leaf block wearing it; false under
    /// open sky or where the canopy was disturbed (see <see cref="PoisonCanopy"/>). With
    /// <paramref name="proxy"/> it reads the species' <c>lod</c> crown proxies instead of the
    /// grown trees, which never runs tree growth (the far rings, <see cref="LodLevelSpec.ProxyCanopy"/>).
    /// Safe to call from any thread.
    /// </summary>
    public static bool TryGetCanopy(int x, int z, bool proxy, out int top, out byte leaf)
    {
        top = 0;
        leaf = BlockType.Air;
        if (CanopyDisturbed.ContainsKey((x, z)))
            return false;

        var entry = proxy
            ? GetProxyCanopyColumns(x >> Chunk.SizeShift, z >> Chunk.SizeShift)
            : GetCanopyColumns(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
        var i = ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);
        top = entry.Tops[i];
        if (top <= 0)
            return false;
        leaf = entry.LeafBlocks[i];
        return true;
    }

    /// <summary>
    /// Marks the canopy columns within Chebyshev <paramref name="radius"/> of (x, z) as disturbed
    /// (see <see cref="CanopyTop"/>). A single mined or blasted wood or leaf cell passes
    /// <see cref="TreeGenerator.SingleEditPoisonRadius"/>; a felled tree passes <see cref="TreeGenerator.FellPoisonRadius"/>
    /// once at the break, which covers its whole canopy.
    /// </summary>
    public static void PoisonCanopy(int x, int z, int radius)
    {
        // One bound check per call, not per cell: ConcurrentDictionary.Count takes every lock, and
        // the limit only bounds memory, so overshooting it by one call's square is harmless.
        if (CanopyDisturbed.Count >= WorldGenerationConfig.CanopyDisturbedLimit)
            CanopyDisturbed.Clear();

        for (var dx = -radius; dx <= radius; dx++)
        for (var dz = -radius; dz <= radius; dz++)
            CanopyDisturbed[(x + dx, z + dz)] = 0;

        Interlocked.Increment(ref _canopyEditVersion);
    }

    private static int _canopyEditVersion;

    /// <summary>Bumped once per <see cref="PoisonCanopy"/> call, so a save can tell whether anything changed.</summary>
    internal static int CanopyEditVersion => Volatile.Read(ref _canopyEditVersion);

    /// <summary>Every felled-canopy marker, for the per-world LOD edit save (<c>LodEditState</c>).</summary>
    internal static (int X, int Z)[] ExportCanopyDisturbed() => CanopyDisturbed.Keys.ToArray();

    /// <summary>
    /// Adds saved felled-canopy markers to the live set (a marker is a plain flag, so the union is
    /// the merge). Call before <c>LodManager.Start</c> so the first region builds see it. Leaves
    /// <see cref="CanopyEditVersion"/> alone: the imported markers are what the file holds.
    /// </summary>
    internal static void ImportCanopyDisturbed(IEnumerable<(int X, int Z)> columns)
    {
        foreach (var column in columns)
            CanopyDisturbed.TryAdd(column, 0);
    }

    internal static int[] GetCanopyTops(int chunkX, int chunkZ) => GetCanopyColumns(chunkX, chunkZ).Tops;

    internal static CanopyColumns GetCanopyColumns(int chunkX, int chunkZ) =>
        CanopyCache.GetOrAdd((chunkX, chunkZ), BuildCanopyFor);

    internal static CanopyColumns GetProxyCanopyColumns(int chunkX, int chunkZ) =>
        ProxyCanopyCache.GetOrAdd((chunkX, chunkZ), BuildProxyCanopyFor);

    // Raises each ground column under a nearby tree's leaf cells to the highest leaf cell above
    // it. Trunks up to ReachHorizontal outside the chunk column still overhang it. The canopy is
    // the BATCH canopy -- the same cell walk Stamp does, over the same TreeBatch shapes -- so it is
    // what the chunks stamp, and the first LOD ring agrees with the full-detail chunks that
    // replace it. The far rings never come here: they read the per-species proxy instead.
    private static CanopyColumns BuildCanopyTops(int chunkX, int chunkZ)
    {
        var r = TreeGenerator.ReachHorizontal;
        var x0 = chunkX * Chunk.Size;
        var z0 = chunkZ * Chunk.Size;
        var x1 = x0 + Chunk.Size;
        var z1 = z0 + Chunk.Size;
        var tops = new int[Chunk.Size * Chunk.Size];
        var leaves = new byte[Chunk.Size * Chunk.Size];

        var cx0 = TreeGenerator.CellOf(x0 - r);
        var cx1 = TreeGenerator.CellOf(x1 - 1 + r);
        var cz0 = TreeGenerator.CellOf(z0 - r);
        var cz1 = TreeGenerator.CellOf(z1 - 1 + r);
        for (var cx = cx0; cx <= cx1; cx++)
        for (var cz = cz0; cz <= cz1; cz++)
        foreach (var shape in TreeBatches.BatchOf(cx, cz).Trees)
        {
            var species = shape.Species;
            if (!species.HasLeaves)
                continue;
            if (shape.X + r < x0 || shape.X - r >= x1 || shape.Z + r < z0 || shape.Z - r >= z1)
                continue;

            var cells = shape.Cells;
            for (var c = shape.WoodCount; c < cells.Length; c++)
            {
                var lx = cells[c].X - x0;
                var lz = cells[c].Z - z0;
                if ((uint)lx >= Chunk.Size || (uint)lz >= Chunk.Size)
                    continue;

                var i = (lx << Chunk.SizeShift) | lz;
                if (cells[c].Y > tops[i])
                {
                    tops[i] = cells[c].Y;
                    leaves[i] = species.LeavesId;
                }
            }
        }

        return new CanopyColumns(tops, leaves);
    }

    // The proxy canopy of a chunk column: every leafy trunk TryGetTree accepts within ProxyReach
    // (the ONE placement source, shared with the real path) raises the columns under its proxy
    // crown (LodCrownProxy) to base + offset, highest wins. Never calls ShapeOf or BatchOf, so the
    // far sweep runs no growth.
    private static CanopyColumns BuildProxyCanopyTops(int chunkX, int chunkZ)
    {
        var x0 = chunkX * Chunk.Size;
        var z0 = chunkZ * Chunk.Size;
        var tops = new int[Chunk.Size * Chunk.Size];
        var leaves = new byte[Chunk.Size * Chunk.Size];

        for (var tx = x0 - TreeGenerator.ProxyReach; tx < x0 + Chunk.Size + TreeGenerator.ProxyReach; tx++)
        for (var tz = z0 - TreeGenerator.ProxyReach; tz < z0 + Chunk.Size + TreeGenerator.ProxyReach; tz++)
        {
            if (!TreeGenerator.TryGetTree(tx, tz, out var species, out var baseY) || !species.HasLeaves)
                continue;

            var height = TreeGenerator.CrownHeightOf(species, tx, tz);
            var radius = species.CrownRadius;
            for (var dx = -radius; dx <= radius; dx++)
            for (var dz = -radius; dz <= radius; dz++)
            {
                var lx = tx + dx - x0;
                var lz = tz + dz - z0;
                if ((uint)lx >= Chunk.Size || (uint)lz >= Chunk.Size)
                    continue;
                var offset = LodCrownProxy.TopOffset(species.LodProfile, height, radius, dx, dz);
                if (offset < 1)
                    continue;

                var i = (lx << Chunk.SizeShift) | lz;
                if (baseY + offset > tops[i])
                {
                    tops[i] = baseY + offset;
                    leaves[i] = species.LeavesId;
                }
            }
        }

        return new CanopyColumns(tops, leaves);
    }
}
