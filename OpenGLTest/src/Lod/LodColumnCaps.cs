using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using VoxelEngine.Chunks;
using VoxelEngine.Chunks.Sources;

namespace VoxelEngine.Lod;

/// <summary>The process-global mined-column far-LOD caps (MIN semantics), the twin of <see cref="LodSurface"/>'s overrides; see the comment on the table below.</summary>
public static class LodColumnCaps
{
    // Destroying blocks exposes the LOD buried beneath the surface. These per-column caps
    // force the LOD height below every removed block so the LOD always stays hidden.
    // They are one half of a pair: the cap has MIN semantics (it only ever falls, so a hole dug
    // below the surface can never expose the shell), while LodSurface's per-column override has
    // REPLACE semantics (the actual top height and block after a placement, a replacement or a
    // removal of the top). A fine column's LOD height is min(override ?? Heights, cap), and
    // BuildMeshData applies both before erosion and HeightBias, so the nesting invariants hold.
    // Bucketed by chunk column (item C9): the value is a Chunk.Size * Chunk.Size array of caps
    // indexed exactly like a heightmap (((x & SizeMask) << SizeShift) | (z & SizeMask)), NoCap
    // where the column is uncapped. A region build then resolves one bucket per prefetched
    // heightmap instead of hashing an (x, z) tuple per height sample, so an unedited region pays
    // nothing and an edited one pays one probe per chunk column, not one per sample.
    // Process-global but per WORLD: the caps record that world's edits, so WorldSeed.Apply resets
    // them between worlds (ResetColumnCaps), and a save carries them across a restart through
    // LodEditState (lod-edits.bin: ExportColumnCaps / ImportColumnCaps).
    private static readonly ConcurrentDictionary<(int CX, int CZ), int[]> ColumnCaps = new();

    /// <summary>Whether no column carries a cap, the case every unedited region build skips on.</summary>
    internal static bool IsEmpty => ColumnCaps.IsEmpty;

    /// <summary>The cap bucket of chunk column (cx, cz), or null when it holds none.</summary>
    internal static int[]? BucketOf(int cx, int cz) =>
        ColumnCaps.TryGetValue((cx, cz), out var bucket) ? bucket : null;

    /// <summary>
    /// Forgets every column cap, which belongs to the previous world's edits; only
    /// <see cref="WorldSeed.Apply"/> calls it.
    /// </summary>
    internal static void ResetColumnCaps() => ColumnCaps.Clear();

    // "Never capped". A cap is always a removed block's Y minus one, so int.MaxValue can never
    // compare below a real terrain height and the uncapped case needs no separate test.
    internal const int NoCap = int.MaxValue;

    private static int[] NewCapBucket()
    {
        var bucket = new int[Chunk.Size * Chunk.Size];
        Array.Fill(bucket, NoCap);
        return bucket;
    }

    /// <summary>
    /// Caps a column's LOD height below a removed block, judged against worldgen's terrain
    /// (<see cref="GeneratedWorldSource"/>). Returns false when the LOD terrain already sat below
    /// the removed block and no region rebuild is needed.
    /// </summary>
    public static bool LowerColumnBelow(int x, int y, int z) =>
        LowerColumnBelow(GeneratedWorldSource.Instance, x, y, z);

    /// <summary>
    /// Caps a column's LOD height below a removed block, judged against the column's terrain
    /// height from <paramref name="source"/> (the world's <see cref="ChunkManager.WorldSource"/>)
    /// when it has no surface override. Returns false when the LOD terrain already sat below the
    /// removed block and no region rebuild is needed.
    /// </summary>
    public static bool LowerColumnBelow(IWorldSource source, int x, int y, int z)
    {
        var cx = x >> Chunk.SizeShift;
        var cz = z >> Chunk.SizeShift;
        var local = ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);
        // The column's current top: a surface override (a raised or re-scanned column) if it has
        // one, else the source's terrain height. The cap then applies on top, as before.
        var current = LodSurface.TopOrHeight(source, x, z);

        ColumnCaps.TryGetValue((cx, cz), out var bucket);
        if (bucket != null)
        {
            var cap = Volatile.Read(ref bucket[local]);
            if (cap < current) current = cap;   // NoCap never wins, so this is the old Math.Min
        }

        // Removed block was above the LOD-relevant terrain (e.g. a player-placed block)
        if (y > current)
            return false;

        // Caps are written on the main thread and read on LodGen. The bucket reference publishes
        // through the ConcurrentDictionary; Volatile.Write orders the element write behind it, which
        // is strictly stronger than the plain dictionary write this replaces, so a future
        // multi-threaded LodGen (item C5) stays safe.
        bucket ??= ColumnCaps.GetOrAdd((cx, cz), static _ => NewCapBucket());
        Volatile.Write(ref bucket[local], y - 1);
        Interlocked.Increment(ref _capEditVersion);
        return true;
    }

    /// <summary>
    /// Whether a column's <paramref name="cap"/> lowers its <paramref name="height"/>: below it, but
    /// within <see cref="StreamingConfig.LodSurfaceRescanDepth"/> (a deeper dig is out of the shell's
    /// sight, see <see cref="LodSurface.RecordEdit"/>). Builds read caps through this, so an older
    /// save's deep cap draws no shaft.
    /// </summary>
    internal static bool Applies(int cap, int height) =>
        cap < height && cap >= height - StreamingConfig.LodSurfaceRescanDepth - 1;

    private static int _capEditVersion;

    /// <summary>Bumped by every cap <see cref="LowerColumnBelow(int, int, int)"/> writes, so a save can tell whether anything changed.</summary>
    internal static int CapEditVersion => Volatile.Read(ref _capEditVersion);

    /// <summary>
    /// A copy of every cap bucket that caps at least one column, for the per-world LOD edit save
    /// (<c>LodEditState</c>). Main thread (the only writer), so the copies are consistent.
    /// </summary>
    internal static List<(int CX, int CZ, int[] Values)> ExportColumnCaps()
    {
        var result = new List<(int CX, int CZ, int[] Values)>();
        foreach (var (key, bucket) in ColumnCaps)
        {
            var copy = new int[bucket.Length];
            var any = false;
            for (var i = 0; i < copy.Length; i++)
            {
                copy[i] = Volatile.Read(ref bucket[i]);
                any |= copy[i] != NoCap;
            }

            if (any) result.Add((key.CX, key.CZ, copy));
        }

        return result;
    }

    /// <summary>
    /// Merges saved caps into the live table. Min semantics like every cap write: the lower cap
    /// wins, so an import can never raise a live edit's cap. Call before <c>LodManager.Start</c> so
    /// the first region builds see it; regions built later pick it up on their own. Leaves
    /// <see cref="CapEditVersion"/> alone: the imported caps are already what the file holds.
    /// </summary>
    internal static void ImportColumnCaps(IEnumerable<(int CX, int CZ, int[] Values)> caps)
    {
        foreach (var (cx, cz, values) in caps)
        {
            int[]? bucket = null;
            var n = Math.Min(values.Length, Chunk.Size * Chunk.Size);
            for (var i = 0; i < n; i++)
            {
                var cap = values[i];
                if (cap == NoCap) continue;

                bucket ??= ColumnCaps.GetOrAdd((cx, cz), static _ => NewCapBucket());
                if (cap < Volatile.Read(ref bucket[i]))
                    Volatile.Write(ref bucket[i], cap);
            }
        }
    }
}
