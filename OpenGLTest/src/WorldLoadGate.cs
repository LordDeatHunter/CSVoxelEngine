using System;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine;

/// <summary>
/// Decides when a loading world may start playing: every chunk inside a sphere of
/// <c>radiusChunks</c> around the camera chunk is generated and meshed, or
/// <c>timeoutSeconds</c> of accumulated update time passed. GL-free; main thread only.
/// </summary>
/// <remarks>
/// <para>
/// A chunk counts once <see cref="Chunk.IsMeshed"/> is true, not once it has a mesh: IsMeshed is
/// also true for an all-air or fully enclosed chunk whose mesh is empty, so a spawn in the sky, an
/// ocean or a cave is ready as soon as it has been built. Meshing a chunk needs all six face
/// neighbors generated, which the <see cref="WorldGenerationConfig.ChunkRadius"/> load sphere
/// provides for any radius up to <c>ChunkRadius - 1</c>, so the sphere can always complete.
/// </para>
/// <para>
/// The far LOD never gates loading: it is built by its own worker and only streams in behind the
/// near chunks, which bury it anyway. The sphere walk (343 probes at radius 3) runs only while a
/// World is loading, never in the playing hot path.
/// </para>
/// </remarks>
/// <param name="radiusChunks">Radius of the ready sphere, in chunks.</param>
/// <param name="timeoutSeconds">Accumulated update time after which the load completes anyway.</param>
public sealed class WorldLoadGate(int radiusChunks, double timeoutSeconds)
{
    /// <summary>Share of the ready sphere that is ready, 0..1. A running maximum: it never
    /// decreases (a chunk leaving the sphere does not move the bar back), and it is forced to 1
    /// when the load completes.</summary>
    public float Progress { get; private set; }

    /// <summary>Update time accumulated so far, in seconds.</summary>
    public double Elapsed { get; private set; }

    /// <summary>Whether the timeout has passed, whatever the sphere's state.</summary>
    public bool TimedOut => Elapsed >= timeoutSeconds;

    /// <summary>Whether the sphere was ready or the timeout passed at the last update.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>A chunk is ready once its block data is generated and it has been meshed.</summary>
    public static bool IsChunkReady(Chunk? chunk) => chunk is { IsBlockDataReady: true, IsMeshed: true };

    /// <summary>
    /// Counts the ready chunks inside the sphere of <paramref name="radiusChunks"/> around the chunk
    /// holding <paramref name="cameraPosition"/>, and the sphere's size.
    /// </summary>
    public static (int Ready, int Total) Count(ChunkManager chunks, Vector3 cameraPosition, int radiusChunks)
    {
        var camChunk = (cameraPosition / Chunk.Size).Floor();
        var cx = (int)camChunk.X;
        var cy = (int)camChunk.Y;
        var cz = (int)camChunk.Z;
        var r = Math.Max(0, radiusChunks);
        var rSq = r * r;

        var ready = 0;
        var total = 0;
        for (var dx = -r; dx <= r; dx++)
        for (var dy = -r; dy <= r; dy++)
        for (var dz = -r; dz <= r; dz++)
        {
            if (dx * dx + dy * dy + dz * dz > rSq) continue;
            total++;
            if (chunks.Chunks.TryGetValue(cx + dx, cy + dy, cz + dz, out var chunk) && IsChunkReady(chunk))
                ready++;
        }

        return (ready, total);
    }

    /// <summary>
    /// Advances the timeout clock by <paramref name="deltaTime"/> (a negative delta is ignored),
    /// re-counts the sphere and returns <see cref="IsComplete"/>.
    /// </summary>
    public bool Update(ChunkManager chunks, Vector3 cameraPosition, double deltaTime)
    {
        Elapsed += Math.Max(0, deltaTime);

        var (ready, total) = Count(chunks, cameraPosition, radiusChunks);
        Progress = Math.Max(Progress, total == 0 ? 1f : ready / (float)total);
        IsComplete = ready >= total || TimedOut;
        if (IsComplete)
            Progress = 1f;

        return IsComplete;
    }
}
