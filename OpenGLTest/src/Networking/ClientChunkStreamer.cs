using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Drives a multiplayer client's chunk loading from the server instead of worldgen: each update it
/// asks the server for the chunks around the camera that are not loaded yet (distance-ordered, a
/// bounded number per update, re-asked until they arrive) and, with every chunk it asks for, for
/// the 3x3 chunk columns around it. A column carries the terrain the mesher reads beyond the blocks
/// (<see cref="ColumnTerrain"/>: heights, water levels and corner climate), and column requests are
/// re-sent like chunk requests until they are answered. Incoming <c>ChunkData</c>/<c>ColumnTerrain</c>
/// payloads are applied here, and the streamer is the client's <see cref="IColumnSource"/>. Main
/// thread only (apart from the column reads, which are thread-safe); it never generates anything
/// locally.
/// </summary>
public sealed class ClientChunkStreamer : IColumnSource
{
    // How long before an unanswered chunk or column request is sent again (the server may not have
    // the chunk loaded yet; a loaded one answers immediately).
    internal const double ResendSeconds = 1.0;

    // Requests per update, so a cold start primes the load sphere over a few frames instead of a burst.
    private const int MaxChunkRequestsPerUpdate = 16;

    // Request-timestamp counts past which PruneStale looks for entries that fell out of range.
    private const int ChunkAskedPruneCount = 4096;
    private const int ColumnAskedPruneCount = 1024;

    private readonly ChunkManager _chunks;
    private readonly Action<int, int, int> _requestChunk;
    private readonly Action<int, int> _requestColumn;
    private readonly Dictionary<(int X, int Y, int Z), double> _chunkAsked = new();
    private readonly Dictionary<(int X, int Z), double> _columnAsked = new();
    // Read by mesh builds on the thread pool (ColumnSource -> ChunkLighting.FillColumns), written here
    // on the main thread, hence concurrent. A stored column's arrays are never edited: a changed
    // column replaces the whole value.
    private readonly ConcurrentDictionary<(int X, int Z), ColumnTerrain> _columns = new();
    private double _time;

    public ClientChunkStreamer(ChunkManager chunks, Action<int, int, int> requestChunk, Action<int, int> requestColumn)
    {
        _chunks = chunks;
        _requestChunk = requestChunk;
        _requestColumn = requestColumn;
        // Everything the client reads from terrain beyond the blocks comes from the server's
        // columns, and a chunk is not meshed until the nine columns around it arrived.
        _chunks.ColumnSource = this;
    }

    /// <summary>Whether the server's terrain for chunk column (<paramref name="chunkX"/>,
    /// <paramref name="chunkZ"/>) has arrived.</summary>
    public bool IsReady(int chunkX, int chunkZ) => _columns.ContainsKey((chunkX, chunkZ));

    /// <summary>The server's terrain for the chunk column, or false while it has not arrived.</summary>
    public bool TryGet(int chunkX, int chunkZ, out ColumnTerrain terrain) =>
        _columns.TryGetValue((chunkX, chunkZ), out terrain);

    private static readonly Vector3[] Offsets = BuildOffsets();

    /// <summary>Requests the not-yet-loaded chunks around <paramref name="cameraPos"/>, nearest first.</summary>
    public void Update(Vector3 cameraPos, double deltaTime)
    {
        _time += deltaTime;

        var camX = MathUtils.FloorToInt(cameraPos.X) >> Chunk.SizeShift;
        var camY = MathUtils.FloorToInt(cameraPos.Y) >> Chunk.SizeShift;
        var camZ = MathUtils.FloorToInt(cameraPos.Z) >> Chunk.SizeShift;

        var budget = MaxChunkRequestsPerUpdate;
        foreach (var offset in Offsets)
        {
            var cx = camX + (int)offset.X;
            var cy = camY + (int)offset.Y;
            var cz = camZ + (int)offset.Z;
            var pos = new Vector3(cx, cy, cz);
            if (_chunks.Chunks.ContainsKey(pos))
                continue;

            if (budget <= 0)
                break;

            if (AskedRecently(cx, cy, cz))
                continue;

            // Every chunk asked for also asks for the 3x3 chunk columns around it (ahead of the
            // chunk, so it can usually mesh on arrival): a build needs all nine (light volume,
            // water surface codes, the +1 tint corners), and a chunk at the load sphere's rim has
            // diagonal columns no chunk inside the sphere ever asks for on its own. A column that
            // already arrived is never asked again (every answer re-dirties the chunks around it);
            // an unanswered one is re-sent after ResendSeconds like a chunk, because a chunk does
            // not mesh without its columns and a lost answer must not strand them. Asking with the
            // chunk request rather than for every unloaded chunk the scan passes keeps the work per
            // update bounded by the request budget.
            for (var dx = -1; dx <= 1; dx++)
            for (var dz = -1; dz <= 1; dz++)
                RequestColumn(cx + dx, cz + dz);

            _chunkAsked[(cx, cy, cz)] = _time;
            _requestChunk(cx, cy, cz);
            budget--;
        }

        PruneStale();
    }

    private void RequestColumn(int cx, int cz)
    {
        if (_columns.ContainsKey((cx, cz)) || ColumnAskedRecently(cx, cz))
            return;
        _columnAsked[(cx, cz)] = _time;
        _requestColumn(cx, cz);
    }

    /// <summary>Restores a chunk from a <c>ChunkData</c> payload and schedules it for meshing.</summary>
    public void ApplyChunk(int cx, int cy, int cz, byte[] payload)
    {
        _chunkAsked.Remove((cx, cy, cz));
        _chunks.ApplyStreamedChunk(new Vector3(cx, cy, cz), ChunkNetCodec.Decode(payload));
    }

    /// <summary>
    /// Stores a column's terrain from a <c>ColumnTerrain</c> payload and re-meshes the loaded chunks
    /// whose build reads it (its own column and the eight neighbours), so their sunlight, water
    /// surface codes and tint follow the server's terrain. A repeat answer equal in heights, water
    /// levels and climate changes nothing and re-meshes nothing. Returns whether the stored terrain
    /// changed.
    /// </summary>
    public bool ApplyColumn(int cx, int cz, byte[] payload)
    {
        var terrain = ChunkNetCodec.DecodeColumn(payload);
        _columnAsked.Remove((cx, cz));
        if (_columns.TryGetValue((cx, cz), out var stored) && stored.ContentEquals(terrain))
            return false;
        _columns[(cx, cz)] = terrain;

        foreach (var chunk in _chunks.Chunks.Values)
        {
            var chunkX = (int)chunk.Position.X;
            var chunkZ = (int)chunk.Position.Z;
            if (Math.Abs(chunkX - cx) <= 1 && Math.Abs(chunkZ - cz) <= 1)
                _chunks.MarkDirty(chunk.Position);
        }

        return true;
    }

    private bool AskedRecently(int cx, int cy, int cz) =>
        _chunkAsked.TryGetValue((cx, cy, cz), out var at) && _time - at < ResendSeconds;

    private bool ColumnAskedRecently(int cx, int cz) =>
        _columnAsked.TryGetValue((cx, cz), out var at) && _time - at < ResendSeconds;

    // Drop request timestamps for chunks and columns that fell out of range, so the maps stay
    // bounded by the load sphere rather than growing for the whole session.
    private void PruneStale()
    {
        var cutoff = _time - ResendSeconds * 4;
        Prune(_chunkAsked, ChunkAskedPruneCount, cutoff);
        Prune(_columnAsked, ColumnAskedPruneCount, cutoff);
    }

    private static void Prune<TKey>(Dictionary<TKey, double> asked, int minCount, double cutoff) where TKey : notnull
    {
        if (asked.Count < minCount)
            return;
        List<TKey>? stale = null;
        foreach (var (key, at) in asked)
            if (at < cutoff)
                (stale ??= []).Add(key);
        if (stale == null)
            return;
        foreach (var key in stale)
            asked.Remove(key);
    }

    // Every chunk offset within the load sphere, nearest first (the same shape the generation
    // worker scans, built once).
    private static Vector3[] BuildOffsets()
    {
        var radius = WorldGenerationConfig.ChunkRadius;
        var offsets = new List<(int Dist2, Vector3 Offset)>();
        for (var dx = -radius; dx <= radius; dx++)
        for (var dy = -radius; dy <= radius; dy++)
        for (var dz = -radius; dz <= radius; dz++)
        {
            var d2 = dx * dx + dy * dy + dz * dz;
            if (d2 <= radius * radius)
                offsets.Add((d2, new Vector3(dx, dy, dz)));
        }

        offsets.Sort(static (a, b) => a.Dist2.CompareTo(b.Dist2));
        var result = new Vector3[offsets.Count];
        for (var i = 0; i < result.Length; i++)
            result[i] = offsets[i].Offset;
        return result;
    }
}
