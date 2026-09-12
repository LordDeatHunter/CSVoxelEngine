using System;
using System.Numerics;
using System.Threading;

namespace silkgltriangle;

/// <summary>
/// The shared resume point for the chunk-generation workers' closest-first offset scan
/// (PERF_TODO C3). All <see cref="WorldGenerationConfig.MaxConcurrentBlockDataGenerations"/>
/// threads of one <see cref="ChunkGenerationWorker"/> share a single instance, so the ~17k-entry
/// sorted-offset list is walked roughly once per camera centre instead of once per call per thread.
///
/// <para><b>1. The invariant.</b> The whole state is one immutable node
/// <c>(Center C, Version V, Index K)</c>, swapped as a unit so a centre, a version and an index can
/// never be read apart. If the node is <c>(C, V, K)</c> then every offset index below <c>K</c> was
/// observed not-needed by a call that had sampled exactly centre <c>C</c> and removal version
/// <c>V</c>. A caller trusts that prefix only when its own freshly sampled <c>(centre, version)</c>
/// pair matches the node's; on any mismatch it scans from index 0.</para>
///
/// <para><b>2. Why the trusted prefix is still valid.</b> <see cref="ChunkManager.NeedsChunk"/> is
/// <c>!Chunks.ContainsKey(p) &amp;&amp; !_queuedChunkPositions.ContainsKey(p)</c>, so it can only go
/// false-to-true for a position once that position has left BOTH dictionaries. Adding to either
/// only makes a position less needed, which is the safe direction. ChunkManager.UnloadDistantChunks
/// is the only place in the tree that removes from either (ChunkManager.cs:200 and :210), and it
/// bumps <see cref="ChunkManager.ChunkRemovalVersion"/> AFTER the loop, once every dropped position
/// has left both. So between one bump and the next, the only positions that can turn needed belong
/// to an unload pass currently in flight - and that pass's own bump necessarily follows, which
/// invalidates every node still carrying the pre-bump version.</para>
///
/// <para><b>3. The bounded imprecision - a cost, not a hazard.</b> A call that sampled the pre-bump
/// version can therefore skip a position that turned needed mid-pass. It is skipped by that one
/// call: the bump discards the node (nobody matches its version any more) and the next call scans
/// from 0. The worst case is latency - a worker that returned null just inside the window sleeps
/// <see cref="WorldGenerationConfig.GenerationDelayIdleMs"/> (200 ms) before rescanning - which is
/// exactly the hole the pre-change full scan already had (sweep past a position, the position turns
/// needed, sleep 200 ms). Meanwhile the other three workers sample after the bump and find it
/// immediately. No position can be skipped permanently.</para>
///
/// <para><b>4. The camera-reverses case.</b> C to C' and back to C, with a worker never observing
/// C', is closed by the version and not by the centre: the unload pass at C' bumped the version, so
/// the node is discarded even though the centre reads equal again. That also demotes the
/// coincidence that generation (ChunkGenerationWorker.BuildSortedOffsets) and unload
/// (ChunkManager.cs:197) share <see cref="WorldGenerationConfig.ChunkRadiusSq"/> from a correctness
/// premise to a performance one: the shared radius is now only why the frontier SURVIVES a still
/// camera, not why it is safe.</para>
///
/// <para><b>5. A hit publishes <c>i</c>, not <c>i + 1</c>.</b> Only indices this call actually proved
/// not-needed are published. It costs one re-test of the returned offset on the next call, and in
/// exchange there is nothing to reason about regarding whether the caller's QueueChunk succeeded,
/// was deduped by another worker, or threw.</para>
///
/// <para><b>6. A failed exchange is dropped, never retried.</b> Retrying is the only way this method
/// could spin, so it does not. Losing the race only shrinks the frontier (someone scans more),
/// never skips. A late publish carrying a stale version is harmless for the same reason it is
/// discarded: nobody whose version sample is current will match it, so everyone full-scans until a
/// current node is published. And <c>index &lt;= start</c> publishes nothing at all, so a converged
/// worker polling every 200 ms allocates no node and executes no loop body.</para>
///
/// <para><b>7. Threads disagreeing about the centre.</b> <c>Camera.Position</c> is an unsynchronised
/// property (Camera.cs:20) read by all four workers, so they can sample different centres within a
/// frame. Each mismatch just resets that call to index 0 - the behaviour this class replaced - so
/// disagreement degrades to the pre-C3 full scan and never below it. A NaN centre never compares
/// equal and therefore always full-scans.</para>
///
/// <para><b>8. Safe publication.</b> The node's three fields are readonly and written before the
/// exchange, and <c>Interlocked.CompareExchange</c> is a full fence, so a
/// reader that sees the reference sees all three fields.</para>
///
/// <para><b>9. The wake model is unchanged.</b> <see cref="FindNext"/> introduces no waiting, no
/// signalling and no sleeping; ChunkGenerationWorker.WorkerLoop keeps its existing 200 ms idle
/// poll. There is no wake to miss because there is no wake.</para>
///
/// <para><b>10. Standing obligation.</b> If a future change removes a position from
/// <see cref="ChunkManager.Chunks"/> or ChunkManager._queuedChunkPositions anywhere other than
/// UnloadDistantChunks without bumping <see cref="ChunkManager.ChunkRemovalVersion"/>, the frontier
/// can starve that chunk. <see cref="ChunkManager.Chunks"/> is public and MeshingTests.cs:49 already
/// removes through it, so the single-removal-site property is a convention, not something the type
/// system enforces. ChunkScanFrontierTests.FrontierTrustsThePrefixUntilTheRemovalVersionMoves and
/// ChunkManagerTests.UnloadBumpsChunkRemovalVersionOnlyWhenAChunkLeaves are the two facts that
/// guard it.</para>
/// </summary>
internal sealed class ChunkScanFrontier(Vector3[] offsets)
{
    /// <summary>
    /// The centre, removal version and resume index, immutable and swapped together so no reader can
    /// observe an index paired with the wrong centre or version.
    /// </summary>
    private sealed class Node(Vector3 center, int version, int index)
    {
        internal readonly Vector3 Center = center;
        internal readonly int Version = version;
        internal readonly int Index = index;
    }

    private Node? _node;

    /// <summary>
    /// The first offset from <paramref name="center"/>, in closest-first order, for which
    /// <paramref name="isNeeded"/> is true - or null when none is. Resumes from the shared frontier
    /// when that frontier was computed for the same centre and the same removal version, and scans
    /// from the closest offset otherwise. See the type doc for the invariant this rests on; the
    /// caller must sample both <paramref name="center"/> and <paramref name="version"/> ONCE, up
    /// front, before any probe.
    /// </summary>
    internal Vector3? FindNext(Vector3 center, int version, Func<Vector3, bool> isNeeded)
    {
        var seen = Volatile.Read(ref _node);
        var start = seen is not null && seen.Center == center && seen.Version == version ? seen.Index : 0;

        for (var i = start; i < offsets.Length; i++)
        {
            var pos = center + offsets[i];
            if (isNeeded(pos))
            {
                // i, not i + 1: only what this call proved not-needed is published (point 5).
                Publish(seen, center, version, start, i);
                return pos;
            }
        }

        Publish(seen, center, version, start, offsets.Length);
        return null;
    }

    /// <summary>
    /// Offers a new frontier node, keeping it only if nobody else moved the frontier since this call
    /// read it. Never retried on failure (point 6), and nothing is published when the call proved no
    /// new prefix.
    /// </summary>
    private void Publish(Node? comparand, Vector3 center, int version, int start, int index)
    {
        if (index <= start) return;
        Interlocked.CompareExchange(ref _node, new Node(center, version, index), comparand);
    }

    /// <summary>How many offsets a full scan walks. For the facts.</summary>
    internal int OffsetCount => offsets.Length;

    /// <summary>
    /// The published resume index, for the facts. It cannot distinguish "nothing published yet" from
    /// "published index 0" - both read 0 - which is why the moved-centre fact keys on
    /// <see cref="PublishedCenter"/> instead.
    /// </summary>
    internal int PublishedIndex => Volatile.Read(ref _node)?.Index ?? 0;

    /// <summary>The centre the published node was computed for, or null when nothing is published.</summary>
    internal Vector3? PublishedCenter => Volatile.Read(ref _node)?.Center;
}
