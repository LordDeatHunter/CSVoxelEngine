using System;
using System.Numerics;
using System.Threading;

namespace silkgltriangle;

/// <summary>
/// The chunk positions the world streams around: one per connected player on a server, the camera's
/// otherwise. <see cref="ChunkManager"/> keeps a chunk loaded while it lies inside the load radius of
/// ANY focus, and <see cref="ChunkGenerationWorker"/> generates around every focus in turn.
/// <para>
/// The foci are published as one immutable array (<see cref="Chunks"/>), written on the main thread
/// and read by the generation workers, and never mutated after it is published. A new array is
/// published only when the set really changed - a different count, or any focus in another chunk -
/// so a still camera allocates nothing and keeps the same reference. Both consumers lean on that:
/// the chunk manager's unload gate compares the reference to skip the scan on frames nothing moved,
/// and each worker's <see cref="ChunkScanFrontier"/> survives while its centre stays equal. Eyes that
/// share a chunk are kept once, in first-seen order, so two players standing together cost one
/// focus.
/// </para>
/// </summary>
public sealed class StreamingFocus
{
    private Vector3[] _chunks = [];

    // Main thread only: the candidate set SetEye / SetEyes builds before comparing it with the
    // published one, reused so an unchanged set allocates nothing.
    private Vector3[] _scratch = new Vector3[4];

    /// <summary>
    /// The focus chunk positions, in chunk units. An immutable snapshot: read it once and keep the
    /// array for the whole pass, since a later read may return a different one. Empty until the
    /// first <see cref="SetEye"/> or <see cref="SetEyes"/>.
    /// </summary>
    public Vector3[] Chunks => Volatile.Read(ref _chunks);

    /// <summary>A single focus at the chunk holding <paramref name="eye"/>. Main thread only.</summary>
    public void SetEye(Vector3 eye)
    {
        _scratch[0] = ChunkOf(eye);
        Publish(1);
    }

    /// <summary>
    /// One focus per distinct chunk holding one of <paramref name="eyes"/>, in first-seen order.
    /// Main thread only.
    /// </summary>
    public void SetEyes(ReadOnlySpan<Vector3> eyes)
    {
        if (_scratch.Length < eyes.Length)
            _scratch = new Vector3[Math.Max(eyes.Length, _scratch.Length * 2)];

        var count = 0;
        foreach (var eye in eyes)
        {
            var chunk = ChunkOf(eye);
            if (Array.IndexOf(_scratch, chunk, 0, count) < 0)
                _scratch[count++] = chunk;
        }

        Publish(count);
    }

    /// <summary>
    /// The squared chunk distance from <paramref name="chunkPos"/> to the nearest focus, or
    /// <see cref="float.PositiveInfinity"/> when there is none.
    /// </summary>
    public static float MinDistanceSquared(Vector3[] foci, Vector3 chunkPos)
    {
        var best = float.PositiveInfinity;
        foreach (var focus in foci)
        {
            var distSq = Vector3.DistanceSquared(focus, chunkPos);
            if (distSq < best) best = distSq;
        }

        return best;
    }

    /// <summary>
    /// Whether <paramref name="chunkPos"/> lies within <paramref name="radiusSq"/> (squared chunk
    /// distance, inclusive) of at least one focus. False when there is none.
    /// </summary>
    public static bool WithinRadius(Vector3[] foci, Vector3 chunkPos, int radiusSq)
    {
        foreach (var focus in foci)
        {
            if (Vector3.DistanceSquared(focus, chunkPos) <= radiusSq)
                return true;
        }

        return false;
    }

    private static Vector3 ChunkOf(Vector3 eye) => (eye / Chunk.Size).Floor();

    /// <summary>
    /// Publishes the first <paramref name="count"/> scratch entries as a new array, unless they equal
    /// the published one entry for entry. A NaN entry never compares equal, so it republishes every
    /// call - the same every-frame rescan a NaN camera chunk always caused.
    /// </summary>
    private void Publish(int count)
    {
        var current = _chunks;
        if (current.Length == count)
        {
            var same = true;
            for (var i = 0; i < count; i++)
            {
                if (current[i] != _scratch[i])
                {
                    same = false;
                    break;
                }
            }

            if (same) return;
        }

        var next = new Vector3[count];
        Array.Copy(_scratch, next, count);
        Volatile.Write(ref _chunks, next);
    }
}
