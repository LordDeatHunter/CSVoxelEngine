using System.Collections.Generic;

namespace silkgltriangle;

/// <summary>
/// Recycles the opaque vertex and index lists a chunk mesh build fills. Each build otherwise
/// allocates a ~200 KB <c>List&lt;Vertex&gt;(4096)</c> on the large object heap, which is what
/// turns a streaming burst into gen2 collections. Builds rent on the mesh workers and the
/// main thread (<see cref="ChunkManager.ImmediateRemesh"/>); lists come back only on the main
/// thread once <see cref="Chunk.UpdateMesh"/> has copied them to the GPU, or when a finished
/// build is dropped as stale — never on the worker, because a completed
/// <see cref="ChunkMeshData"/> can wait in the upload queue across frames.
/// The translucent lists are not pooled: they start at zero capacity and most chunks never
/// grow them.
/// </summary>
public sealed class MeshBufferPool
{
    public const int VertexCapacity = 4096;
    public const int IndexCapacity = 6144;

    // Bounds what an idle pool keeps alive. Up to MaxDirtyChunksPerFrame builds start in one
    // frame and at most MaxMeshUploadsPerFrame lists come back per frame, so a burst has about
    // a hundred lists in flight; a cap of 16 missed a third of all rents. 128 vertex lists at
    // the rent capacity are ~25 MB.
    private const int MaxPooled = 128;

    // A list that outgrew its rent capacity keeps the larger array, so the next canopy chunk it
    // serves does not regrow it; but a few dense chunks reach 30k+ vertices (a 3 MB array), and
    // over a long session every pooled list would drift to that size. Anything past this ceiling
    // (786 KB of vertices) is left to the GC instead.
    private const int MaxRetainedVertexCapacity = VertexCapacity * 4;
    private const int MaxRetainedIndexCapacity = IndexCapacity * 4;

    private readonly Stack<List<Vertex>> _vertices = new();
    private readonly Stack<List<ushort>> _indices = new();
    private readonly object _lock = new();

    public List<Vertex> RentVertices()
    {
        lock (_lock)
        {
            if (_vertices.Count > 0) return _vertices.Pop();
        }
        return new List<Vertex>(VertexCapacity);
    }

    public List<ushort> RentIndices()
    {
        lock (_lock)
        {
            if (_indices.Count > 0) return _indices.Pop();
        }
        return new List<ushort>(IndexCapacity);
    }

    /// <summary>
    /// Takes the opaque lists of a build that is finished with (uploaded or discarded). Lists
    /// below the rent capacity — the empty-mesh placeholders — and lists that grew past
    /// <see cref="MaxRetainedVertexCapacity"/> are left to the GC.
    /// </summary>
    public void Return(ChunkMeshData data)
    {
        var vertices = data.Vertices;
        var indices = data.Indices;
        lock (_lock)
        {
            if (vertices.Capacity >= VertexCapacity && vertices.Capacity <= MaxRetainedVertexCapacity
                && _vertices.Count < MaxPooled)
            {
                vertices.Clear();
                _vertices.Push(vertices);
            }
            if (indices.Capacity >= IndexCapacity && indices.Capacity <= MaxRetainedIndexCapacity
                && _indices.Count < MaxPooled)
            {
                indices.Clear();
                _indices.Push(indices);
            }
        }
    }

    /// <summary>Vertex lists currently idle in the pool (tests).</summary>
    public int PooledVertexLists
    {
        get { lock (_lock) return _vertices.Count; }
    }
}
