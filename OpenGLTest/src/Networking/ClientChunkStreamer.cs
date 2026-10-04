using System;
using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Drives a multiplayer client's chunk loading from the server instead of worldgen: each update it
/// asks the server for the chunks around the camera that are not loaded yet (distance-ordered, a
/// bounded number per update, re-asked until they arrive) and for the chunk-column heights the
/// mesher needs to light them. Incoming <c>ChunkData</c>/<c>ColumnHeights</c> payloads are applied
/// here. Main thread only; it never generates anything locally.
/// </summary>
public sealed class ClientChunkStreamer
{
    // How long before an unanswered request is sent again (the server may not have the chunk loaded
    // yet; a loaded one answers immediately).
    private const double ResendSeconds = 1.0;

    // Requests per update, so a cold start primes the load sphere over a few frames instead of a burst.
    private const int MaxChunkRequestsPerUpdate = 16;

    private readonly ChunkManager _chunks;
    private readonly Action<int, int, int> _requestChunk;
    private readonly Action<int, int> _requestColumn;
    private readonly Dictionary<(int X, int Y, int Z), double> _chunkAsked = new();
    private readonly Dictionary<(int X, int Z), double> _columnAsked = new();
    private readonly Dictionary<(int X, int Z), int[]> _columns = new();
    private double _time;

    public ClientChunkStreamer(ChunkManager chunks, Action<int, int, int> requestChunk, Action<int, int> requestColumn)
    {
        _chunks = chunks;
        _requestChunk = requestChunk;
        _requestColumn = requestColumn;
        // The mesher lights streamed chunks from these heights instead of worldgen.
        _chunks.ColumnHeightSource = (cx, cz) => _columns.TryGetValue((cx, cz), out var heights) ? heights : null;
    }

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

            // Ask for the column once per chunk column; the mesher needs all nine around a chunk.
            if (NotAsked(_columnAsked, (cx, cz)))
            {
                _columnAsked[(cx, cz)] = _time;
                _requestColumn(cx, cz);
            }

            if (AskedRecently(cx, cy, cz))
                continue;
            _chunkAsked[(cx, cy, cz)] = _time;
            _requestChunk(cx, cy, cz);
            budget--;
        }

        PruneStale();
    }

    /// <summary>Restores a chunk from a <c>ChunkData</c> payload and schedules it for meshing.</summary>
    public void ApplyChunk(int cx, int cy, int cz, byte[] payload)
    {
        _chunkAsked.Remove((cx, cy, cz));
        _chunks.ApplyStreamedChunk(new Vector3(cx, cy, cz), ChunkNetCodec.Decode(payload));
    }

    /// <summary>
    /// Stores a column's heights from a <c>ColumnHeights</c> payload and re-meshes the loaded chunks
    /// whose light volume covers it (its own column and the eight neighbours), so their sunlight is
    /// re-seeded now that the height is known.
    /// </summary>
    public void ApplyColumn(int cx, int cz, byte[] payload)
    {
        _columns[(cx, cz)] = ChunkNetCodec.DecodeHeights(payload);
        _columnAsked.Remove((cx, cz));

        foreach (var chunk in _chunks.Chunks.Values)
        {
            var chunkX = (int)chunk.Position.X;
            var chunkZ = (int)chunk.Position.Z;
            if (Math.Abs(chunkX - cx) <= 1 && Math.Abs(chunkZ - cz) <= 1)
                _chunks.MarkDirty(chunk.Position);
        }
    }

    private bool AskedRecently(int cx, int cy, int cz) =>
        _chunkAsked.TryGetValue((cx, cy, cz), out var at) && _time - at < ResendSeconds;

    private static bool NotAsked<TKey>(Dictionary<TKey, double> asked, TKey key) where TKey : notnull =>
        !asked.ContainsKey(key);

    // Drop request timestamps for chunks that fell out of range, so the maps stay bounded by the
    // load sphere rather than growing for the whole session.
    private void PruneStale()
    {
        if (_chunkAsked.Count < 4096)
            return;
        var cutoff = _time - ResendSeconds * 4;
        List<(int, int, int)>? stale = null;
        foreach (var (key, at) in _chunkAsked)
            if (at < cutoff)
                (stale ??= []).Add(key);
        if (stale == null)
            return;
        foreach (var key in stale)
            _chunkAsked.Remove(key);
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
