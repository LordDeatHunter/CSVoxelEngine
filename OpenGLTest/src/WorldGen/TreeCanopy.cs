using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using VoxelEngine.Chunks;
using VoxelEngine.Lod;
using VoxelEngine.WorldGen.Trees.Shape;

namespace VoxelEngine.WorldGen;

/// <summary>
/// The far-LOD canopy over the tree worldgen: the grown canopy tops per chunk column (the batch
/// canopy the first LOD ring reads), the species' crown proxies the farther rings read, and the
/// felled-canopy markers player edits leave (<see cref="PoisonCanopy"/>) and the crowns a reactive
/// growth added back (<see cref="SetCanopyAdded"/>), which the per-world LOD edit save carries. Lookups are safe from any thread; the markers are written on the main
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
    internal sealed class CanopyColumns(int[] tops, ushort[] leafBlocks)
    {
        public readonly int[] Tops = tops;
        public readonly ushort[] LeafBlocks = leafBlocks;
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
    // than resurrecting a chopped crown at long range. Entries are gameplay truth and are never
    // evicted: one bit per fine column, kept per chunk column (4 words of 64 bits, indexed like
    // the canopy arrays), so a fell costs a handful of 32-byte entries instead of a thousand
    // dictionary entries, and nothing is ever cleared wholesale. The words are set with an atomic
    // Or on the main thread and read lock-free by the LOD builders.
    // Process-global but per WORLD: WorldSeed.Apply resets it between worlds (ResetCanopyDisturbed),
    // and a save carries it across a restart through LodEditState (lod-edits.bin).
    private static readonly ConcurrentDictionary<(int X, int Z), ulong[]> CanopyDisturbed = new();

    private const int DisturbedWords = Chunk.Size * Chunk.Size / 64;

    // Static delegate, not a lambda at the call site (see BuildShapeFor).
    private static readonly Func<(int X, int Z), ulong[]> NewDisturbedWords = static _ => new ulong[DisturbedWords];

    // Canopy a reactive growth added (TreeGrowthWriter): per fine column, the grown tree's real top
    // leaf cell and its leaf block, packed by LodSurface.Pack into a long, LodSurface.None where
    // none. The generated canopy knows nothing of a tree the player grew, so without this a grown
    // crown stays invisible at range until a reload. It wins over a disturbed marker (a grow
    // poisons its columns and then adds the real top back) and over the generated canopy, and
    // PoisonCanopy clears it, so a later mine, fell, blast or decay removes the grown crown again.
    // Gameplay truth like CanopyDisturbed, so it never enters a GenerationalCache; same publish
    // order as LodSurface (the bucket through the dictionary, each element by Volatile.Write).
    private static readonly ConcurrentDictionary<(int X, int Z), long[]> CanopyAdded = new();

    private static int _canopyAddedVersion;

    private static long[] NewAddedBucket()
    {
        var bucket = new long[Chunk.Size * Chunk.Size];
        Array.Fill(bucket, LodSurface.None);
        return bucket;
    }

    private static int FineIndex(int x, int z) => ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);

    private static bool TryGetAdded(int x, int z, out int top, out ushort leaf)
    {
        top = 0;
        leaf = BlockType.Air;
        if (!CanopyAdded.TryGetValue((x >> Chunk.SizeShift, z >> Chunk.SizeShift), out var bucket))
            return false;
        var value = Volatile.Read(ref bucket[FineIndex(x, z)]);
        if (value == LodSurface.None)
            return false;
        LodSurface.Unpack(value, out top, out leaf);
        // A crown at sky-island height is above the far LOD's reach (an older save may hold one)
        if (top >= WorldGenerationConfig.SkyIslandBottomY)
            return false;
        // A leaf block this run no longer knows (a save read it back as air) is no crown at all
        return leaf != BlockType.Air;
    }

    private static void ClearAdded(int x, int z)
    {
        if (!CanopyAdded.TryGetValue((x >> Chunk.SizeShift, z >> Chunk.SizeShift), out var bucket))
            return;
        ref var slot = ref bucket[FineIndex(x, z)];
        if (Volatile.Read(ref slot) == LodSurface.None)
            return;
        Volatile.Write(ref slot, LodSurface.None);
        Interlocked.Increment(ref _canopyAddedVersion);
    }

    private static bool IsDisturbed(int x, int z)
    {
        if (!CanopyDisturbed.TryGetValue((x >> Chunk.SizeShift, z >> Chunk.SizeShift), out var words))
            return false;
        var i = FineIndex(x, z);
        return (Volatile.Read(ref words[i >> 6]) & (1UL << (i & 63))) != 0;
    }

    private static void MarkDisturbed(int x, int z)
    {
        var words = CanopyDisturbed.GetOrAdd((x >> Chunk.SizeShift, z >> Chunk.SizeShift), NewDisturbedWords);
        var i = FineIndex(x, z);
        Interlocked.Or(ref words[i >> 6], 1UL << (i & 63));
    }

    /// <summary>Drops the cached canopy tops (real and proxy); called by <see cref="TreeGenerator.ResetCaches"/> only.</summary>
    internal static void ResetCaches()
    {
        CanopyCache.Clear();
        ProxyCanopyCache.Clear();
    }

    /// <summary>
    /// Forgets every felled-canopy marker and every added grown crown, which belong to the previous
    /// world's edits; only <see cref="WorldSeed.Apply"/> calls it.
    /// </summary>
    internal static void ResetCanopyDisturbed()
    {
        CanopyDisturbed.Clear();
        CanopyAdded.Clear();
    }

    /// <summary>
    /// Highest canopy cell over the ground column (x, z), or 0 under open sky. Disturbed
    /// columns (see <see cref="PoisonCanopy"/>) read 0 unless a grown crown was added back over
    /// them (<see cref="SetCanopyAdded"/>). Safe to call from any thread.
    /// </summary>
    public static int CanopyTop(int x, int z)
    {
        if (TryGetAdded(x, z, out var added, out _))
            return added;
        if (IsDisturbed(x, z))
            return 0;

        return GetCanopyTops(x >> Chunk.SizeShift, z >> Chunk.SizeShift)
            [((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask)];
    }

    public static ushort CanopyLeafBlock(int x, int z)
    {
        if (TryGetAdded(x, z, out _, out var addedLeaf))
            return addedLeaf;
        if (IsDisturbed(x, z))
            return BlockType.Air;

        TryGetCanopy(x, z, out _, out var leaf);
        return leaf;
    }

    /// <summary>The grown canopy over (x, z); see <see cref="TryGetCanopy(int, int, bool, out int, out ushort)"/>.</summary>
    public static bool TryGetCanopy(int x, int z, out int top, out ushort leaf) =>
        TryGetCanopy(x, z, proxy: false, out top, out leaf);

    /// <summary>
    /// The canopy top over the ground column (x, z) and the leaf block wearing it; false under
    /// open sky or where the canopy was disturbed (see <see cref="PoisonCanopy"/>). A grown crown
    /// added over the column (<see cref="SetCanopyAdded"/>) answers first, on every ring. With
    /// <paramref name="proxy"/> it reads the species' <c>lod</c> crown proxies instead of the
    /// grown trees, which never runs tree growth (the far rings, <see cref="LodLevelSpec.ProxyCanopy"/>).
    /// Safe to call from any thread.
    /// </summary>
    public static bool TryGetCanopy(int x, int z, bool proxy, out int top, out ushort leaf)
    {
        if (TryGetAdded(x, z, out top, out leaf))
            return true;
        top = 0;
        leaf = BlockType.Air;
        if (IsDisturbed(x, z))
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
    /// once at the break, which covers its whole canopy. It also clears any grown crown added over
    /// those columns (<see cref="SetCanopyAdded"/>): add after poisoning, never before.
    /// </summary>
    public static void PoisonCanopy(int x, int z, int radius)
    {
        for (var dx = -radius; dx <= radius; dx++)
        for (var dz = -radius; dz <= radius; dz++)
        {
            MarkDisturbed(x + dx, z + dz);
            ClearAdded(x + dx, z + dz);
        }

        Interlocked.Increment(ref _canopyEditVersion);
    }

    /// <summary>
    /// Records that a reactive growth left the crown of ground column (x, z) at leaf cell
    /// <paramref name="top"/> wearing <paramref name="leaf"/>, so the far LOD draws it. Call after
    /// <see cref="PoisonCanopy"/> for the column, which is what clears an older record. Main
    /// thread. Returns false when the column already held exactly this record.
    /// </summary>
    public static bool SetCanopyAdded(int x, int z, int top, ushort leaf)
    {
        var packed = LodSurface.Pack(top, leaf);
        var bucket = CanopyAdded.GetOrAdd((x >> Chunk.SizeShift, z >> Chunk.SizeShift), static _ => NewAddedBucket());
        ref var slot = ref bucket[FineIndex(x, z)];
        if (Volatile.Read(ref slot) == packed)
            return false;

        Volatile.Write(ref slot, packed);
        Interlocked.Increment(ref _canopyAddedVersion);
        return true;
    }

    /// <summary>Bumped by every <see cref="SetCanopyAdded"/> or clear that changes a column, so a save can tell whether anything changed.</summary>
    internal static int CanopyAddedVersion => Volatile.Read(ref _canopyAddedVersion);

    /// <summary>A copy of every added-canopy bucket that holds a record, for the per-world LOD edit save (<c>LodEditState</c>). Main thread.</summary>
    internal static List<(int CX, int CZ, long[] Values)> ExportCanopyAdded()
    {
        var result = new List<(int CX, int CZ, long[] Values)>();
        foreach (var (key, bucket) in CanopyAdded)
        {
            var copy = new long[bucket.Length];
            var any = false;
            for (var i = 0; i < copy.Length; i++)
            {
                copy[i] = Volatile.Read(ref bucket[i]);
                any |= copy[i] != LodSurface.None;
            }

            if (any) result.Add((key.X, key.Z, copy));
        }

        return result;
    }

    /// <summary>
    /// Merges saved added-canopy records into the live table; a live record always wins, so an
    /// import never undoes a grow made first. Call before <c>LodManager.Start</c>. Leaves
    /// <see cref="CanopyAddedVersion"/> alone: the imported state is what the file holds.
    /// </summary>
    internal static void ImportCanopyAdded(IEnumerable<(int CX, int CZ, long[] Values)> buckets)
    {
        foreach (var (cx, cz, values) in buckets)
        {
            long[]? bucket = null;
            var n = Math.Min(values.Length, Chunk.Size * Chunk.Size);
            for (var i = 0; i < n; i++)
            {
                if (values[i] == LodSurface.None) continue;
                bucket ??= CanopyAdded.GetOrAdd((cx, cz), static _ => NewAddedBucket());
                if (Volatile.Read(ref bucket[i]) == LodSurface.None)
                    Volatile.Write(ref bucket[i], values[i]);
            }
        }
    }

    private static int _canopyEditVersion;

    /// <summary>Bumped once per <see cref="PoisonCanopy"/> call, so a save can tell whether anything changed.</summary>
    internal static int CanopyEditVersion => Volatile.Read(ref _canopyEditVersion);

    /// <summary>Every felled-canopy marker, for the per-world LOD edit save (<c>LodEditState</c>).</summary>
    internal static (int X, int Z)[] ExportCanopyDisturbed()
    {
        var columns = new List<(int X, int Z)>();
        foreach (var ((cx, cz), words) in CanopyDisturbed)
        for (var i = 0; i < Chunk.Size * Chunk.Size; i++)
        {
            if ((Volatile.Read(ref words[i >> 6]) & (1UL << (i & 63))) != 0)
                columns.Add(((cx << Chunk.SizeShift) + (i >> Chunk.SizeShift), (cz << Chunk.SizeShift) + (i & Chunk.SizeMask)));
        }
        return columns.ToArray();
    }

    /// <summary>
    /// Adds saved felled-canopy markers to the live set (a marker is a plain flag, so the union is
    /// the merge). Call before <c>LodManager.Start</c> so the first region builds see it. Leaves
    /// <see cref="CanopyEditVersion"/> alone: the imported markers are what the file holds.
    /// </summary>
    internal static void ImportCanopyDisturbed(IEnumerable<(int X, int Z)> columns)
    {
        foreach (var (x, z) in columns)
            MarkDisturbed(x, z);
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
        var leaves = new ushort[Chunk.Size * Chunk.Size];

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
        var leaves = new ushort[Chunk.Size * Chunk.Size];

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
