using System.Collections.Generic;

namespace silkgltriangle;

/// <summary>
/// Recycles the vertex and index lists a mesh build fills. Each build otherwise
/// allocates a ~128 KB <c>List&lt;Vertex&gt;(4096)</c> on the large object heap, which is what
/// turns a streaming burst into gen2 collections. Builds rent on the mesh workers and the
/// main thread (<see cref="ChunkManager.ImmediateRemesh"/>); lists come back only on the main
/// thread once <see cref="Chunk.UpdateMesh"/> has copied them to the GPU, or when a finished
/// build is dropped as stale — never on the worker, because a completed
/// <see cref="ChunkMeshData"/> can wait in the upload queue across frames.
/// The translucent lists are pooled separately at a smaller rent: most chunks hold no fluid
/// or glass, so empty ones never touch the pool and ocean chunks rent once at a capacity
/// that already fits a full 16x16 water top.
/// <para>
/// The capacities are instance state because <see cref="LodManager"/> owns a second,
/// LOD-sized pool for the four output lists of a region build (<c>LodRegion.BuildMeshData</c>):
/// a region build is an order of magnitude larger than a chunk build, so one shared pool would
/// either regrow on every LOD rent or drift the chunk rents upward. That pool follows the same
/// contract — rented on the <see cref="WorldGenerationConfig.MaxConcurrentLodGenerations"/>
/// LodGen threads, returned on the main thread after the upload or on the out-of-range drop,
/// never on a worker. Several concurrent renters are safe for the same reason the mesh workers
/// are: every Rent and Return takes the instance lock, and its retention ceilings are sized
/// from the LOD in-flight bound (see <c>LodManager.MeshBuffers</c>).
/// </para>
/// <para>
/// <c>retainEmptyTranslucent</c> exists for that LOD pool. A chunk mesher only fills the
/// translucent lists when the chunk holds fluid or glass, so dropping the empty ones costs
/// nothing; a region build rents its water lists on every build and an inland region fills
/// none, so dropping those would drain the pool instead of recycling it.
/// </para>
/// </summary>
public sealed class MeshBufferPool
{
    public const int VertexCapacity = 4096;
    public const int IndexCapacity = 6144;

    /// <summary>
    /// A full 16x16 water top is 256 quads (1024 vertices / 1536 indices), so an ocean
    /// chunk rents once and never regrows; smaller glass patches just keep the slack.
    /// </summary>
    public const int TranslucentVertexCapacity = 1024;
    public const int TranslucentIndexCapacity = 1536;

    // Bounds what an idle pool keeps alive. Up to MaxDirtyChunksPerFrame builds start in one
    // frame and at most MaxMeshUploadsPerFrame lists come back per frame, so a burst has about
    // a hundred lists in flight; a cap of 16 missed a third of all rents. 128 vertex lists at
    // the rent capacity are ~25 MB.
    private const int MaxPooled = 128;

    // Fewer translucent lists are ever in flight (only water/glass chunks rent), and each
    // one is a quarter of the opaque rent, so the cap is lower.
    private const int MaxPooledTranslucent = 32;

    private readonly int _vertexCapacity;
    private readonly int _indexCapacity;
    private readonly int _translucentVertexCapacity;
    private readonly int _translucentIndexCapacity;
    private readonly int _maxPooled;
    private readonly int _maxPooledTranslucent;
    private readonly bool _retainEmptyTranslucent;

    // A list that outgrew its rent capacity keeps the larger array, so the next canopy chunk it
    // serves does not regrow it; but a few dense chunks reach 30k+ vertices (a 3 MB array), and
    // over a long session every pooled list would drift to that size. Anything past this ceiling
    // (786 KB of vertices at the chunk rent) is left to the GC instead. A list only ever doubles,
    // so four times the rent capacity is two growth steps of headroom.
    private readonly int _maxRetainedVertexCapacity;
    private readonly int _maxRetainedIndexCapacity;
    private readonly int _maxRetainedTranslucentVertexCapacity;
    private readonly int _maxRetainedTranslucentIndexCapacity;

    private readonly Stack<List<Vertex>> _vertices = new();
    private readonly Stack<List<ushort>> _indices = new();
    private readonly Stack<List<Vertex>> _translucentVertices = new();
    private readonly Stack<List<ushort>> _translucentIndices = new();
    private readonly object _lock = new();

    /// <summary>
    /// Every parameter defaults to the chunk-sized pool, so <c>new MeshBufferPool()</c> is
    /// exactly the pool <see cref="ChunkManager"/> has always used. <see cref="LodManager"/>
    /// passes the region-build sizes instead.
    /// </summary>
    public MeshBufferPool(
        int vertexCapacity = VertexCapacity,
        int indexCapacity = IndexCapacity,
        int translucentVertexCapacity = TranslucentVertexCapacity,
        int translucentIndexCapacity = TranslucentIndexCapacity,
        int maxPooled = MaxPooled,
        int maxPooledTranslucent = MaxPooledTranslucent,
        bool retainEmptyTranslucent = false)
    {
        _vertexCapacity = vertexCapacity;
        _indexCapacity = indexCapacity;
        _translucentVertexCapacity = translucentVertexCapacity;
        _translucentIndexCapacity = translucentIndexCapacity;
        _maxPooled = maxPooled;
        _maxPooledTranslucent = maxPooledTranslucent;
        _retainEmptyTranslucent = retainEmptyTranslucent;
        _maxRetainedVertexCapacity = vertexCapacity * 4;
        _maxRetainedIndexCapacity = indexCapacity * 4;
        _maxRetainedTranslucentVertexCapacity = translucentVertexCapacity * 4;
        _maxRetainedTranslucentIndexCapacity = translucentIndexCapacity * 4;
    }

    public List<Vertex> RentVertices()
    {
        lock (_lock)
        {
            if (_vertices.Count > 0) return _vertices.Pop();
        }
        return new List<Vertex>(_vertexCapacity);
    }

    public List<ushort> RentIndices()
    {
        lock (_lock)
        {
            if (_indices.Count > 0) return _indices.Pop();
        }
        return new List<ushort>(_indexCapacity);
    }

    public List<Vertex> RentTranslucentVertices()
    {
        lock (_lock)
        {
            if (_translucentVertices.Count > 0) return _translucentVertices.Pop();
        }
        return new List<Vertex>(_translucentVertexCapacity);
    }

    public List<ushort> RentTranslucentIndices()
    {
        lock (_lock)
        {
            if (_translucentIndices.Count > 0) return _translucentIndices.Pop();
        }
        return new List<ushort>(_translucentIndexCapacity);
    }

    /// <summary>
    /// Takes the lists of a chunk build that is finished with (uploaded or discarded).
    /// </summary>
    public void Return(ChunkMeshData data) =>
        Return(data.Vertices, data.Indices, data.TranslucentVertices, data.TranslucentIndices);

    /// <summary>
    /// Takes the four lists of a build that is finished with (uploaded or discarded). Lists
    /// below the rent capacity — the empty-mesh placeholders — and lists that grew past four
    /// times it are left to the GC. Translucent lists only come back when they actually drew
    /// something, unless the pool was built with <c>retainEmptyTranslucent</c>: most chunks
    /// have no fluid or glass, but a region build rents its water lists every time.
    /// </summary>
    public void Return(List<Vertex> vertices, List<ushort> indices,
                       List<Vertex> translucentVertices, List<ushort> translucentIndices)
    {
        lock (_lock)
        {
            if (vertices.Capacity >= _vertexCapacity && vertices.Capacity <= _maxRetainedVertexCapacity
                && _vertices.Count < _maxPooled)
            {
                vertices.Clear();
                _vertices.Push(vertices);
            }
            if (indices.Capacity >= _indexCapacity && indices.Capacity <= _maxRetainedIndexCapacity
                && _indices.Count < _maxPooled)
            {
                indices.Clear();
                _indices.Push(indices);
            }
            if ((_retainEmptyTranslucent || translucentVertices.Count > 0)
                && translucentVertices.Capacity >= _translucentVertexCapacity
                && translucentVertices.Capacity <= _maxRetainedTranslucentVertexCapacity
                && _translucentVertices.Count < _maxPooledTranslucent)
            {
                translucentVertices.Clear();
                _translucentVertices.Push(translucentVertices);
            }
            if ((_retainEmptyTranslucent || translucentIndices.Count > 0)
                && translucentIndices.Capacity >= _translucentIndexCapacity
                && translucentIndices.Capacity <= _maxRetainedTranslucentIndexCapacity
                && _translucentIndices.Count < _maxPooledTranslucent)
            {
                translucentIndices.Clear();
                _translucentIndices.Push(translucentIndices);
            }
        }
    }

    /// <summary>Vertex lists currently idle in the pool (tests).</summary>
    public int PooledVertexLists
    {
        get { lock (_lock) return _vertices.Count; }
    }

    /// <summary>Translucent vertex lists currently idle in the pool (tests).</summary>
    public int PooledTranslucentVertexLists
    {
        get { lock (_lock) return _translucentVertices.Count; }
    }
}
