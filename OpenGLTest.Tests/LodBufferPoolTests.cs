using System.Linq;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Covers item C8: a region build's four output lists are rented from a LOD-sized
/// <see cref="MeshBufferPool"/> owned by <see cref="LodManager"/> instead of being newed on the
/// LodGen worker, and come back on the main thread — after the GPU upload or on the
/// out-of-range drop. These facts pin the whole contract headlessly: the rent sizes, the
/// LOD-only <c>retainEmptyTranslucent</c> fork, that a build really does recycle, that a real
/// region stays inside the pool's retention ceiling (the sizing mistake a guessed 8192 rent
/// would have made), that a recycled list carries no stale content, and that both
/// <see cref="LodManager.ApplyCompletedMeshes"/> exits return exactly once.
/// </summary>
public sealed class LodBufferPoolTests
{
    public LodBufferPoolTests() => TestContent.EnsureLoaded();

    /// <summary>
    /// A completed build whose lists are rented from <paramref name="pool"/> and left empty, so
    /// <see cref="LodRegion"/>'s constructor skips <c>new Mesh</c> and the apply pass runs with
    /// no GL. Shaped like <c>LodManagerTests.HeadlessRegion</c>'s column arrays.
    /// </summary>
    private static LodMeshData RentedEmptyMesh(MeshBufferPool pool, int level, int x, int z)
    {
        var span = LodRegion.Levels[level].RegionBlocks / Chunk.Size;
        var n = span * span;
        return new LodMeshData(level, x, z,
            pool.RentVertices(), pool.RentIndices(),
            pool.RentTranslucentVertices(), pool.RentTranslucentIndices(),
            0f, 0f, new int[n], new int[n]);
    }

    [Fact]
    public void LodPoolRentsAtRegionCapacity()
    {
        var pool = new LodManager(null!).MeshBuffers;

        Assert.True(pool.RentVertices().Capacity >= LodRegion.MeshVertexCapacity);
        Assert.True(pool.RentIndices().Capacity >= LodRegion.MeshIndexCapacity);
        Assert.True(pool.RentTranslucentVertices().Capacity >= LodRegion.WaterVertexCapacity);
        Assert.True(pool.RentTranslucentIndices().Capacity >= LodRegion.WaterIndexCapacity);
    }

    [Fact]
    public void EmptyLodWaterListsComeBackButChunkOnesDoNot()
    {
        // A region build rents its water lists on every build and an inland region fills none,
        // so the LOD pool must take empty ones back or it drains inland...
        var lod = new LodManager(null!).MeshBuffers;
        lod.Return(lod.RentVertices(), lod.RentIndices(),
            lod.RentTranslucentVertices(), lod.RentTranslucentIndices());
        Assert.Equal(1, lod.PooledTranslucentVertexLists);

        // ...while a chunk mesher only fills them for fluid or glass, so the chunk pool's
        // behavior is unchanged: an untouched translucent list is left to the GC.
        var chunk = new MeshBufferPool();
        chunk.Return(chunk.RentVertices(), chunk.RentIndices(),
            chunk.RentTranslucentVertices(), chunk.RentTranslucentIndices());
        Assert.Equal(0, chunk.PooledTranslucentVertexLists);
    }

    [Fact]
    public void BuildMeshDataRentsAndRecyclesItsLists()
    {
        var pool = new LodManager(null!).MeshBuffers;

        var first = LodRegion.BuildMeshData(0, 0, 0, pool);
        Assert.Equal(0, pool.PooledVertexLists);

        pool.Return(first.Vertices, first.Indices, first.WaterVertices, first.WaterIndices);
        Assert.Equal(1, pool.PooledVertexLists);
        Assert.Equal(1, pool.PooledTranslucentVertexLists);

        var second = LodRegion.BuildMeshData(0, 0, 0, pool);
        Assert.Same(first.Vertices, second.Vertices);
        Assert.Same(first.Indices, second.Indices);
        Assert.Same(first.WaterVertices, second.WaterVertices);
        Assert.Equal(0, pool.PooledVertexLists);
    }

    [Fact]
    public void RealRegionBuildsStayUnderTheRetentionCeiling()
    {
        // The sizing pin. MeshBufferPool drops anything that grew past capacity * 4, so a rent
        // sized below what a real region actually emits would both regrow on every cold build
        // and then be silently refused instead of pooled — the win inverted. Each level is
        // checked through its own pool so the counts are unambiguous.
        for (var level = 0; level < LodRegion.Levels.Length; level++)
        {
            var pool = new LodManager(null!).MeshBuffers;
            var mesh = LodRegion.BuildMeshData(level, 0, 0, pool);
            Assert.NotEmpty(mesh.Vertices);
            // The decisive half of the pin: a real build must fit its rent outright. A list only
            // ever doubles, so an undersized rent (the 8192 literal these consts replaced) regrows
            // into a fresh LOH array on every cold build - exactly the allocation C8 removes - and
            // lands nearer the retention ceiling, where the busiest regions stop being pooled at
            // all. Capacity comes back equal to the rent only while the rent is big enough.
            Assert.Equal(LodRegion.MeshVertexCapacity, mesh.Vertices.Capacity);
            Assert.Equal(LodRegion.MeshIndexCapacity, mesh.Indices.Capacity);
            Assert.Equal(LodRegion.WaterVertexCapacity, mesh.WaterVertices.Capacity);
            Assert.Equal(LodRegion.WaterIndexCapacity, mesh.WaterIndices.Capacity);

            pool.Return(mesh.Vertices, mesh.Indices, mesh.WaterVertices, mesh.WaterIndices);
            Assert.Equal(1, pool.PooledVertexLists);
            Assert.Equal(1, pool.PooledTranslucentVertexLists);
        }
    }

    [Fact]
    public void PooledBuildMatchesUnpooledBuild()
    {
        // A recycled list is cleared, not reset, so a build on one must produce byte-for-byte
        // what a freshly allocated one does — no stale tail, no leftover count.
        var pool = new LodManager(null!).MeshBuffers;
        var warmup = LodRegion.BuildMeshData(0, 0, 0, pool);
        pool.Return(warmup.Vertices, warmup.Indices, warmup.WaterVertices, warmup.WaterIndices);

        var reused = LodRegion.BuildMeshData(0, 0, 0, pool);
        var fresh = LodRegion.BuildMeshData(0, 0, 0);

        Assert.Equal(fresh.Vertices.Count, reused.Vertices.Count);
        Assert.Equal(fresh.Indices.Count, reused.Indices.Count);
        Assert.Equal(fresh.WaterVertices.Count, reused.WaterVertices.Count);
        // SequenceEqual, not Assert.Equal(IEnumerable, IEnumerable): Vertex is a struct of
        // float-bearing Vector3/Vector2 fields, so xUnit's comparer would fall back to
        // reflective ValueType.Equals for each of ~21k elements.
        Assert.True(fresh.Vertices.SequenceEqual(reused.Vertices));
        Assert.True(fresh.Indices.SequenceEqual(reused.Indices));
        Assert.True(fresh.WaterVertices.SequenceEqual(reused.WaterVertices));
    }

    [Fact]
    public void CompletedMeshesReturnTheirBuffers()
    {
        // One camera region per LOD level; ApplyCompletedMeshes indexes it by data.Level.
        var camRegions = new[] { (0, 0), (0, 0), (0, 0) };

        // Upload path: the region is in range, goes to the GPU (no Mesh is built, the lists are
        // empty) and its buffers recycle on the same line.
        var uploads = new LodManager(null!);
        var uploaded = RentedEmptyMesh(uploads.MeshBuffers, 0, 0, 0);
        uploads.EnqueueCompletedMesh(uploaded);
        uploads.ApplyCompletedMeshes(camRegions);

        Assert.True(uploads.Regions.ContainsKey((0, 0, 0)));
        Assert.Equal(1, uploads.MeshBuffers.PooledVertexLists);
        Assert.Same(uploaded.Vertices, uploads.MeshBuffers.RentVertices());

        // Drop path: the region left the camera's range while it waited, so nothing is built
        // from it — and its buffers must come back all the same.
        var drops = new LodManager(null!);
        var dropped = RentedEmptyMesh(drops.MeshBuffers, 0, 1000, 1000);
        drops.EnqueueCompletedMesh(dropped);
        drops.ApplyCompletedMeshes(camRegions);

        Assert.True(drops.Regions.IsEmpty);
        Assert.Equal(1, drops.MeshBuffers.PooledVertexLists);
        Assert.Same(dropped.Vertices, drops.MeshBuffers.RentVertices());
    }
}
