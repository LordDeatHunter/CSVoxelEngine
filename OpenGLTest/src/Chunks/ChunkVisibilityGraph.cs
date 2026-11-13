using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks.Meshing;

namespace VoxelEngine.Chunks;

/// <summary>
/// The cave cull: which loaded chunks a line of sight from the camera's chunk can reach through open cells, walked
/// over the chunks' face-to-face visibility sets (<see cref="ChunkVisibility"/>, the Minecraft "advanced cave culling"
/// search). From the camera's chunk every face is open; a chunk entered through one face is left only through the
/// faces its set joins to it, and never back against a direction the path already took, so the walk stays a
/// front that only moves away from the camera. A chunk not loaded or not meshed yet counts as open, so a streaming
/// hole never hides what is past it. The chunk passes draw only the chunks it reached (<see cref="Sees"/>): a cave
/// sealed off from the camera, or the underside of the surface seen from inside the rock, is never drawn. The walk
/// re-runs only when the camera changes chunk, the meshed set changes or a chunk's set changes. Main thread only.
/// </summary>
public sealed class ChunkVisibilityGraph(ChunkMap chunks)
{
    // One past the load radius, so the walk reaches the outermost loaded chunks
    private const int Reach = StreamingConfig.ChunkRadius + 1;
    private const int Span = 2 * Reach + 1;

    private readonly int[] _visited = new int[Span * Span * Span];
    private readonly Queue<(int X, int Y, int Z, int Entry, int Dirs)> _queue = new(4096);
    private int _stamp;
    private Vector3 _lastCamChunk = new(float.NaN);
    private int _lastMeshVersion = -1;
    private int _lastVisibilityVersion = -1;

    /// <summary>Whether <paramref name="chunk"/> was reached by the last walk, and so may be drawn.</summary>
    public bool Sees(Chunk chunk) => !PostProcessConfig.ChunkCaveCullEnabled || chunk.CaveStamp == _stamp;

    /// <summary>
    /// Re-walks the graph from the chunk holding <paramref name="eye"/> when the camera changed chunk or
    /// <paramref name="meshVersion"/> / <paramref name="visibilityVersion"/> moved (see <see cref="ChunkRenderLists"/>).
    /// </summary>
    public void Update(Vector3 eye, int meshVersion, int visibilityVersion)
    {
        var camChunk = (eye / Chunk.Size).Floor();
        if (camChunk == _lastCamChunk && meshVersion == _lastMeshVersion && visibilityVersion == _lastVisibilityVersion)
            return;
        _lastCamChunk = camChunk;
        _lastMeshVersion = meshVersion;
        _lastVisibilityVersion = visibilityVersion;
        Walk((int)camChunk.X, (int)camChunk.Y, (int)camChunk.Z);
    }

    private void Walk(int cx, int cy, int cz)
    {
        // A fresh stamp marks both the visited cells and the chunks reached; wrapping back to 0 clears the array
        if (++_stamp == int.MaxValue)
        {
            Array.Clear(_visited);
            _stamp = 1;
        }

        _queue.Clear();
        Visit(cx, cy, cz, cx, cy, cz);
        Mark(cx, cy, cz);
        for (var face = 0; face < ChunkVisibility.Faces; face++)
            Enqueue(cx, cy, cz, cx, cy, cz, face, 0);

        while (_queue.TryDequeue(out var node))
        {
            var set = Mark(node.X, node.Y, node.Z);
            for (var face = 0; face < ChunkVisibility.Faces; face++)
            {
                // Never back against a direction the path already took
                if ((node.Dirs >> (face ^ 1) & 1) != 0) continue;
                if (!ChunkVisibility.Connected(set, node.Entry, face)) continue;
                Enqueue(cx, cy, cz, node.X, node.Y, node.Z, face, node.Dirs);
            }
        }
    }

    private void Enqueue(int cx, int cy, int cz, int x, int y, int z, int face, int dirs)
    {
        var (dx, dy, dz) = ChunkVisibility.Step(face);
        var (nx, ny, nz) = (x + dx, y + dy, z + dz);
        if (!Visit(cx, cy, cz, nx, ny, nz)) return;
        _queue.Enqueue((nx, ny, nz, face ^ 1, dirs | 1 << face));
    }

    // Claims the cell at (x, y, z) for this walk; false when it is out of reach or already claimed
    private bool Visit(int cx, int cy, int cz, int x, int y, int z)
    {
        var (ox, oy, oz) = (x - cx, y - cy, z - cz);
        if (ox * ox + oy * oy + oz * oz > Reach * Reach) return false;
        var i = ((ox + Reach) * Span + oy + Reach) * Span + oz + Reach;
        if (_visited[i] == _stamp) return false;
        _visited[i] = _stamp;
        return true;
    }

    // Stamps the chunk at (x, y, z) as reached and returns its set: open for a chunk not loaded or not meshed yet
    private ulong Mark(int x, int y, int z)
    {
        if (!chunks.TryGetValue(x, y, z, out var chunk)) return ChunkVisibility.All;
        chunk.CaveStamp = _stamp;
        return chunk.IsMeshed ? chunk.Visibility : ChunkVisibility.All;
    }
}
