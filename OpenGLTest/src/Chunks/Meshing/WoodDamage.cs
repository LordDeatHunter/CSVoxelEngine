using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace VoxelEngine.Chunks.Meshing;

/// <summary>
/// The permanent chop damage of the natural trunk cells the local player is cutting: per world cell,
/// the damage dealt so far and the side face the first hit landed on. Damage on a natural tree
/// (never a placed, detached log) is kept, not reset when the player stops, so a half-felled tree stays
/// half-felled, and it is saved with the world. The mesher reads it (on its pool threads) when it emits a
/// full-cube wood cell, so a damaged cell is drawn with its hit face eaten away from the viewer's left edge
/// to the right as the damage rises (<see cref="TreeWoodEmitter.EmitEaten"/>); the hitbox and collision
/// stay the whole cube. Per world, owned by <see cref="ChunkManager"/>, written by the local player's
/// mining only (the main thread); a client and a server hold none.
/// </summary>
public sealed class WoodDamage
{
    /// <summary>Columns (pixels of the 16 across a cell) the cut can eat along the face.</summary>
    public const int FaceColumns = 16;

    /// <summary>The face stored for a hit that was not on a side (top or bottom): the cell shows no bite.</summary>
    public const int NoFace = 4;

    /// <summary>One damaged cell: its damage and its bite face (0..3, or <see cref="NoFace"/>).</summary>
    public readonly record struct Entry(float Damage, int Face);

    private readonly ConcurrentDictionary<long, Entry> _cells = new();
    private int _version;

    /// <summary>Moves on every change, so an autosave can skip rewriting the file while it stands still.</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>Whether any cell holds damage.</summary>
    public bool IsEmpty => _cells.IsEmpty;

    /// <summary>
    /// How many of the face's <see cref="FaceColumns"/> columns a block that has taken
    /// <paramref name="damage"/> of <paramref name="hardness"/> has had eaten: 0 untouched, at least 1 after
    /// any damage, rising with it, and the whole face only at full damage (the block is gone by then).
    /// </summary>
    public static int EatenColumns(float damage, float hardness)
    {
        if (hardness <= 0f || damage <= 0f) return 0;
        var share = Math.Clamp(damage / hardness, 0f, 1f);
        return Math.Clamp((int)MathF.Ceiling(share * FaceColumns), 1, FaceColumns);
    }

    /// <summary>
    /// Records that the cell has taken <paramref name="damage"/> in all (absolute, not a delta). The bite face
    /// is kept from the first hit that had a side; <paramref name="face"/> (0..3, else <see cref="NoFace"/>)
    /// only fills it in. Returns whether the stored entry changed.
    /// </summary>
    public bool Set(int x, int y, int z, float damage, int face)
    {
        var key = CellKey.Pack(x, y, z);
        if (damage <= 0f) return Remove(key);
        var keep = _cells.TryGetValue(key, out var old) && old.Face != NoFace ? old.Face : face is >= 0 and < NoFace ? face : NoFace;
        var next = new Entry(damage, keep);
        if (_cells.TryGetValue(key, out old) && old == next) return false;
        _cells[key] = next;
        Interlocked.Increment(ref _version);
        return true;
    }

    /// <summary>The cell's entry, false when it holds no damage.</summary>
    public bool TryGet(int x, int y, int z, out Entry entry)
    {
        entry = default;
        return !_cells.IsEmpty && _cells.TryGetValue(CellKey.Pack(x, y, z), out entry);
    }

    /// <summary>Forgets the cell's damage (the block is gone). Returns whether it held any.</summary>
    public bool Clear(int x, int y, int z) => Remove(CellKey.Pack(x, y, z));

    private bool Remove(long key)
    {
        if (!_cells.TryRemove(key, out _)) return false;
        Interlocked.Increment(ref _version);
        return true;
    }

    /// <summary>Every damaged cell as (packed cell key, entry), for the save.</summary>
    public IReadOnlyList<(long Key, Entry Entry)> Export()
    {
        var list = new List<(long, Entry)>(_cells.Count);
        foreach (var pair in _cells) list.Add((pair.Key, pair.Value));
        return list;
    }

    /// <summary>Merges saved entries in: an entry already present (damage dealt since) is kept.</summary>
    public void Import(IEnumerable<(long Key, Entry Entry)> entries)
    {
        foreach (var (key, entry) in entries)
        {
            if (entry.Damage <= 0f || !_cells.TryAdd(key, entry)) continue;
            Interlocked.Increment(ref _version);
        }
    }
}
