using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace silkgltriangle;

/// <summary>
/// Per-column surface overrides that keep the far LOD in step with player edits: the actual top
/// surface-solid height of an edited column and the block wearing it. They sit beside
/// <c>LodRegion</c>'s removal caps and together they decide a fine column's LOD height:
/// <c>min(override.Top ?? generated Heights, cap)</c>. The override has REPLACE semantics (it can
/// raise, lower or re-tile a column); the cap has MIN semantics and only ever falls, so a hole dug
/// below the surface can never expose the buried shell even when a tower is later built over it.
/// <see cref="LodRegion.BuildMeshData"/> feeds both into its fine-height grid BEFORE the erosion
/// window and the height bias, so the nesting and burial invariants hold by construction.
/// </summary>
/// <remarks>
/// Bucketed by chunk column exactly like the caps: a <c>Chunk.Size * Chunk.Size</c> int array
/// indexed like a heightmap, <see cref="None"/> where the column has no override. Written on the
/// main thread, read on the LodGen workers; the bucket publishes through the ConcurrentDictionary
/// and each element through <c>Volatile.Write</c>, the caps' publish order. Process-global but
/// per WORLD: <see cref="WorldSeed.Apply"/> resets it between worlds, and a save carries it
/// across a restart through <c>LodEditState</c> (<see cref="Export"/> / <see cref="Import"/>).
/// </remarks>
public static class LodSurface
{
    private static readonly ConcurrentDictionary<(int CX, int CZ), int[]> Overrides = new();

    private static int _editVersion;

    /// <summary>
    /// "No override". <see cref="Pack"/> could only produce it for a top of -8388608 with block 0,
    /// which no world height reaches.
    /// </summary>
    internal const int None = int.MinValue;

    /// <summary>Top in the high 24 bits (sign kept), block id in the low 8.</summary>
    internal static int Pack(int top, byte block) => (top << 8) | block;

    /// <summary>Inverse of <see cref="Pack"/>: an arithmetic shift keeps a negative top.</summary>
    internal static void Unpack(int value, out int top, out byte block)
    {
        top = value >> 8;
        block = (byte)value;
    }

    /// <summary>Bumped by every <see cref="Set"/> that changes a column, so a save can tell whether anything changed.</summary>
    internal static int EditVersion => Volatile.Read(ref _editVersion);

    /// <summary>Whether no column carries an override, the case every unedited region build skips on.</summary>
    internal static bool IsEmpty => Overrides.IsEmpty;

    /// <summary>The override bucket of chunk column (cx, cz), or null when it holds none.</summary>
    internal static int[]? BucketOf(int cx, int cz) =>
        Overrides.TryGetValue((cx, cz), out var bucket) ? bucket : null;

    private static int[] NewBucket()
    {
        var bucket = new int[Chunk.Size * Chunk.Size];
        Array.Fill(bucket, None);
        return bucket;
    }

    private static int LocalIndex(int x, int z) =>
        ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);

    /// <summary>The override of world column (x, z), if it has one.</summary>
    internal static bool TryGet(int x, int z, out int top, out byte block)
    {
        var bucket = BucketOf(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
        var value = bucket != null ? Volatile.Read(ref bucket[LocalIndex(x, z)]) : None;
        if (value == None)
        {
            top = 0;
            block = 0;
            return false;
        }

        Unpack(value, out top, out block);
        return true;
    }

    /// <summary>
    /// Records that world column (x, z)'s surface is at <paramref name="top"/> wearing
    /// <paramref name="block"/> (0 = keep the generated tile rule). Returns false when the column
    /// already held exactly this override, so a repeated record never bumps
    /// <see cref="EditVersion"/> (which drives lod-edits.bin autosaves) or rebuilds a region.
    /// </summary>
    internal static bool Set(int x, int z, int top, byte block)
    {
        var packed = Pack(top, block);
        var bucket = Overrides.GetOrAdd((x >> Chunk.SizeShift, z >> Chunk.SizeShift), static _ => NewBucket());
        var local = LocalIndex(x, z);
        if (Volatile.Read(ref bucket[local]) == packed) return false;

        Volatile.Write(ref bucket[local], packed);
        Interlocked.Increment(ref _editVersion);
        return true;
    }

    /// <summary>The column's overridden top, or its generated terrain height when it has no override.</summary>
    internal static int TopOrHeight(int x, int z) =>
        TryGet(x, z, out var top, out _)
            ? top
            : ColumnCache.GetHeights(x >> Chunk.SizeShift, z >> Chunk.SizeShift)[LocalIndex(x, z)];

    /// <summary>
    /// Whether a block counts as the LOD surface: a full, solid, occluding cube, the same notion
    /// of "top solid terrain" <c>ColumnData.Heights</c> uses. Translucent ice and glass, cutout
    /// leaves, tree wood, pebbles, flowers, snow layers and fluids never count, so trees stay
    /// with the canopy path and a player-built ice or glass structure never raises the LOD.
    /// </summary>
    internal static bool IsSurfaceSolid(byte id) => BlockType.IsSolid(id) && BlockType.Occludes(id);

    /// <summary>Forgets every override, which belongs to the previous world; only <see cref="WorldSeed.Apply"/> calls it.</summary>
    internal static void Reset() => Overrides.Clear();

    /// <summary>
    /// A copy of every override bucket that overrides at least one column, for the per-world LOD
    /// edit save (<c>LodEditState</c>). Main thread (the only writer), so the copies are consistent.
    /// </summary>
    internal static List<(int CX, int CZ, int[] Values)> Export()
    {
        var result = new List<(int CX, int CZ, int[] Values)>();
        foreach (var (key, bucket) in Overrides)
        {
            var copy = new int[bucket.Length];
            var any = false;
            for (var i = 0; i < copy.Length; i++)
            {
                copy[i] = Volatile.Read(ref bucket[i]);
                any |= copy[i] != None;
            }

            if (any) result.Add((key.CX, key.CZ, copy));
        }

        return result;
    }

    /// <summary>
    /// Merges saved overrides into the live table. A live override always wins: an element is
    /// written only where the column has none yet, so an edit made before the import is never
    /// replaced by the older saved surface. Call before <c>LodManager.Start</c> so the first region
    /// builds see it. Leaves <see cref="EditVersion"/> alone: the imported state is what the file holds.
    /// </summary>
    internal static void Import(IEnumerable<(int CX, int CZ, int[] Values)> overrides)
    {
        foreach (var (cx, cz, values) in overrides)
        {
            int[]? bucket = null;
            var n = Math.Min(values.Length, Chunk.Size * Chunk.Size);
            for (var i = 0; i < n; i++)
            {
                var value = values[i];
                if (value == None) continue;

                // Same publish order as Set: the bucket through the dictionary, the element behind it.
                bucket ??= Overrides.GetOrAdd((cx, cz), static _ => NewBucket());
                if (Volatile.Read(ref bucket[i]) == None)
                    Volatile.Write(ref bucket[i], value);
            }
        }
    }

    /// <summary>
    /// Updates the LOD surface of column (x, z) after the cell (x, y, z) was edited. Main thread
    /// only, called AFTER the edit. Returns whether the effective LOD surface changed (a record
    /// that leaves the override exactly as it was reports false), i.e. whether the caller must
    /// follow with <see cref="LodManager.MarkEditedColumn"/>.
    /// <list type="bullet">
    /// <item><b>Above the top</b>: a surface-solid block raises the override to it; anything else
    /// (a flower, a leaf, a pane of glass) changes nothing.</item>
    /// <item><b>At the top</b>: a surface-solid block is a replacement and swaps the override's
    /// block; anything else means the top was removed, and the column is rescanned downward through
    /// loaded chunks for at most <see cref="WorldGenerationConfig.LodSurfaceRescanDepth"/> cells. An
    /// unloaded cell is assumed solid (the conservative assumption <see cref="LodRegion.LowerColumnBelow"/>'s
    /// y - 1 cap makes too) and becomes the top with the generated tile rule (block 0).</item>
    /// <item><b>Below the top</b>: a dig falls through to the monotone removal cap
    /// (<see cref="LodRegion.LowerColumnBelow"/>), so a hole never exposes the buried shell.</item>
    /// </list>
    /// A placement must only be recorded when the placed block <see cref="IsSurfaceSolid"/>: this
    /// cannot tell a placed torch from a removal, and would read it as a sub-surface dig and cap
    /// the column. Removals are always recorded.
    /// <para>
    /// Call sites — every path that edits a cell must call this (World does it through
    /// World.NotifyLodEdit), so add the next edit path here:
    /// <list type="bullet">
    /// <item>World.RemoveBlock (mining, including the mined cell of a multiblock)</item>
    /// <item>World.FellWood (the lowest felled cell per column)</item>
    /// <item>IItemUseWorld.MarkLodColumnEdited (item uses: the TNT a firestarter primes)</item>
    /// <item>World.PlaceBlockAtFace (surface-solid placements only)</item>
    /// <item>World.TryConvertMultiblock and World.RevertMultiblock (all eight cells)</item>
    /// <item>FallingBlockManager.TryTopple (the highest then the lowest cell of the toppled run)
    /// and its Settle (a surface-solid landing)</item>
    /// <item>TntManager.Detonate (the highest then the lowest blasted cell per column)</item>
    /// <item>KilnManager.Update (the integrity sweep's revert, all eight cells)</item>
    /// <item>FluidManager.Write (block writes above the column's generated water level: lava
    /// hardening, dirt turning to mud)</item>
    /// </list>
    /// Campfire branch piles, stack placements through SetStateAt and bucket fluid edits need no
    /// hook: none of them is ever surface-solid.
    /// </para>
    /// </summary>
    public static bool RecordEdit(ChunkManager chunks, int x, int y, int z)
    {
        if (!chunks.TryGetCell(x, y, z, out var id, out _))
            return false;

        var top = TopOrHeight(x, z);

        if (y > top)
        {
            if (!IsSurfaceSolid(id)) return false;
            return Set(x, z, y, id);
        }

        if (y == top)
        {
            // A replacement: the top stays where it is and only its tile changes. Set reports
            // false when the column already carries exactly this override.
            if (IsSurfaceSolid(id))
                return Set(x, z, y, id);

            // The top was removed: find the next surface below it.
            var depth = WorldGenerationConfig.LodSurfaceRescanDepth;
            for (var yy = y - 1; yy >= y - depth; yy--)
            {
                if (!chunks.TryGetCell(x, yy, z, out var below, out _))
                    return Set(x, z, yy, 0);

                if (IsSurfaceSolid(below))
                    return Set(x, z, yy, below);
            }

            return Set(x, z, y - depth - 1, 0);
        }

        return !IsSurfaceSolid(id) && LodRegion.LowerColumnBelow(x, y, z);
    }
}
