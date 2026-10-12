using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace silkgltriangle;

/// <summary>
/// The loaded-chunk table, keyed internally on a packed <see cref="ChunkKey"/> <c>long</c> so a
/// probe costs one integer hash instead of a three-float <see cref="Vector3"/> hash.
/// <para>
/// <b>Vector3 is and stays the outward key.</b> Every member below takes or returns the same
/// <see cref="Vector3"/> chunk position callers used before, and this wrapper is the whole of the
/// public-surface change: no call site had to be edited to keep compiling. The <c>int</c>
/// overloads exist purely for the sites that already hold the chunk coordinates as integers
/// (<see cref="ChunkManager.GetBlockGlobal"/>, <see cref="Chunk"/>'s neighbor resolve, the LOD
/// column prefetch) and skip the float-to-int conversion the <see cref="Vector3"/> overloads do.
/// </para>
/// <para>
/// Genuinely lock-free, a <see cref="ConcurrentDictionary{TKey,TValue}"/> and never a plain
/// dictionary behind a lock: the table is written from the chunk-generation worker thread
/// (<see cref="ChunkManager.QueueChunk"/>) and read from the mesh-build pool workers as well as
/// the main thread.
/// </para>
/// <para>
/// <see cref="TryRemove(Vector3, out Chunk)"/> is deliberately side-effect free — a plain
/// passthrough with no removal-version bump. The chunk-scan frontier's removal version is bumped
/// by <see cref="ChunkManager"/>'s unload loop alone, and moving that bump in here would change
/// that contract while appearing to strengthen it.
/// </para>
/// <para>
/// There is deliberately no <c>GetEnumerator</c>: callers that want the chunks take
/// <see cref="Values"/>, and the one place that wants the keys takes <see cref="Packed"/>. A
/// future <c>foreach (var kvp in Chunks)</c> is therefore a compile error that forces the choice
/// rather than silently picking one.
/// </para>
/// </summary>
public sealed class ChunkMap
{
    private readonly ConcurrentDictionary<long, Chunk> _map = new();

    /// <summary>The chunk at a chunk position; the setter replaces any chunk already there.</summary>
    public Chunk this[Vector3 pos]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _map[ChunkKey.Pack(pos)];
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _map[ChunkKey.Pack(pos)] = value;
    }

    /// <summary>How many chunks are in the table.</summary>
    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _map.Count;
    }

    /// <summary>
    /// A snapshot of the loaded chunks. Like the underlying dictionary's, this takes a locked
    /// full-table copy, so it is for tests and cold paths — never the render or mesh loops.
    /// </summary>
    public ICollection<Chunk> Values
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _map.Values;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(Vector3 pos, [MaybeNullWhen(false)] out Chunk chunk) =>
        _map.TryGetValue(ChunkKey.Pack(pos), out chunk);

    /// <summary>The <c>int</c> fast path: for callers that already hold chunk coordinates.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(int cx, int cy, int cz, [MaybeNullWhen(false)] out Chunk chunk) =>
        _map.TryGetValue(ChunkKey.Pack(cx, cy, cz), out chunk);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ContainsKey(Vector3 pos) => _map.ContainsKey(ChunkKey.Pack(pos));

    /// <summary>The <c>int</c> fast path: for callers that already hold chunk coordinates.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ContainsKey(int cx, int cy, int cz) => _map.ContainsKey(ChunkKey.Pack(cx, cy, cz));

    /// <summary>
    /// Removes a chunk and hands it back. A plain passthrough: no removal-version bump, no other
    /// side effect — see the type remark.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryRemove(Vector3 pos, [MaybeNullWhen(false)] out Chunk chunk) =>
        _map.TryRemove(ChunkKey.Pack(pos), out chunk);

    /// <summary>
    /// The packed table itself, so <see cref="ChunkManager"/>'s unload scan can enumerate keys
    /// that are already packed — it needs the key for four further dictionary removals per
    /// unloaded chunk, and unpacking only to re-pack would be the one place this wrapper cost
    /// more than it saved.
    /// </summary>
    internal ConcurrentDictionary<long, Chunk> Packed
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _map;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool ContainsPacked(long key) => _map.ContainsKey(key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetPacked(long key, [MaybeNullWhen(false)] out Chunk chunk) =>
        _map.TryGetValue(key, out chunk);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryRemovePacked(long key, [MaybeNullWhen(false)] out Chunk chunk) =>
        _map.TryRemove(key, out chunk);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetPacked(long key, Chunk chunk) => _map[key] = chunk;
}
