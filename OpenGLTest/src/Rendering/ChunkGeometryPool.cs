using System;
using Silk.NET.OpenGL;

namespace silkgltriangle;

/// <summary>
/// A range handed out by a <see cref="ChunkGeometryPool"/>: a run of units that covers both the
/// pool's vertex buffer and its index buffer at the same unit offset. The indices stored in the
/// slice stay <c>ushort</c> and relative to the slice, so a draw addresses them with
/// <c>firstIndex = IndexOffset</c> and <c>baseVertex = VertexOffset</c>.
/// </summary>
/// <param name="UnitOffset">Start of the allocation in the pool's unit space.</param>
/// <param name="UnitCount">Length of the allocation in units (what <see cref="ChunkGeometryPool.Free"/> returns).</param>
/// <param name="VertexCount">Vertices actually written; the tail of the last unit is padding.</param>
/// <param name="IndexCount">Indices actually written, i.e. the draw's element count.</param>
public readonly record struct GeometrySlice(int UnitOffset, int UnitCount, int VertexCount, int IndexCount)
{
    /// <summary>First vertex of the slice, i.e. the draw's <c>baseVertex</c>.</summary>
    public int VertexOffset => UnitOffset * ChunkGeometryPool.VerticesPerUnit;

    /// <summary>First index of the slice, i.e. the draw's <c>firstIndex</c>.</summary>
    public int IndexOffset => UnitOffset * ChunkGeometryPool.IndicesPerUnit;
}

/// <summary>
/// One shared vertex buffer plus one shared index buffer (and the single VAO that points at them)
/// for every chunk mesh of one kind — the render manager owns an opaque pool and a translucent
/// one. Chunks no longer own a <see cref="Mesh"/>: they upload into a pool and keep a
/// <see cref="GeometrySlice"/>, which is what lets a whole pass become a single
/// <c>glMultiDrawElementsIndirect</c> with no buffer or VAO rebinds between chunks.
///
/// Space is handed out by a <see cref="GeometryAllocator"/> in fixed units of
/// <see cref="VerticesPerUnit"/> vertices AND <see cref="IndicesPerUnit"/> indices. One allocator
/// therefore covers both buffers: chunk geometry is entirely quads, exactly 4 vertices to 6
/// indices, so the paired units waste nothing and there is only one free list to reason about
/// (and to test). Fixed units also bound fragmentation, since every hole is a whole number of
/// units and coalesces with its neighbors.
///
/// The buffers are immutable-size <c>glBufferStorage</c> allocations, so growing means allocating
/// a bigger pair and copying the old contents across on the GPU with <c>glCopyBufferSubData</c>:
/// every live slice keeps its unit offset, so no chunk has to be remeshed or re-uploaded and the
/// slices chunks already hold stay valid. Growth doubles, so it happens a handful of times during
/// the first stream-in and then never again.
///
/// Main thread only: uploads come from <c>Chunk.UpdateMesh</c> via <c>ChunkManager.ApplyMesh</c>
/// and frees from <c>Chunk.Dispose</c> via <c>ChunkManager.UnloadDistantChunks</c>, both of which
/// run on the render thread.
/// </summary>
public sealed unsafe class ChunkGeometryPool : IDisposable
{
    /// <summary>Vertices per unit. With <see cref="IndicesPerUnit"/> this is the quad ratio 4:6.</summary>
    public const int VerticesPerUnit = 64;

    /// <summary>Indices per unit — 2048 B of vertices and 192 B of indices per unit.</summary>
    public const int IndicesPerUnit = 96;

    /// <summary>Opaque pool start: 262144 units, i.e. 512 MB of vertices and 48 MB of
    /// indices. Steady state measured 244801/262144 units (93 %), so this starts where
    /// the old 65536-unit pool settled after two mid-stream doubling copies (item 21).
    /// Growth still doubles from here if a view ever needs more.</summary>
    public const int OpaqueInitialUnits = 262144;

    /// <summary>Translucent pool start: 32768 units, i.e. 64 MB of vertices and 6 MB of
    /// indices. The water-heavy fly settles at 65536, so one doubling remains instead of
    /// three; 128 MB permanently for water that may not be on screen is wasteful.</summary>
    public const int TranslucentInitialUnits = 32768;

    /// <summary>LOD pool start: 163840 units, i.e. 320 MB of vertices and 30 MB of indices.
    /// Measured, the way <see cref="OpaqueInitialUnits"/> records 244801/262144: on
    /// <c>perf-fly.json</c> the pool peaked at <b>118004 of 131072</b> units (90 %) at 660
    /// resident regions and never grew across four runs — but 90 % is not headroom. Occupancy
    /// runs at a steady <b>176-181 units per resident region</b> (shell plus sheet), so the 759
    /// resident ceiling below projects to <b>~137400 units</b>, which 131072 does not cover.
    /// 163840 covers that projection with 19 % spare and still leaves the doubling in reserve.
    /// No size makes growth impossible — every region at its level's peak shell plus a full sheet
    /// would be ~331 units each, i.e. ~251000 — so this is sized for the measured world, not the
    /// arithmetic worst case.
    ///
    /// One pool holds both the LOD region shells and their sea sheets: a region's two meshes are
    /// born from one <c>LodMeshData</c> and freed together in one <c>Dispose</c>, so their
    /// lifetimes never diverge and the chunk pools' size-driven opaque/translucent split (512 MB
    /// vs 64 MB) does not transfer.
    ///
    /// Sizing runs off the resident ceiling, not the settled count. The settled live set is 591
    /// regions (197 integer offsets inside RegionRadius 8, times the three <c>LodRegion.Levels</c>),
    /// but generation scans at <c>RegionRadius</c> (LodManager.BuildSortedOffsets) while retention
    /// and the out-of-range upload drop test use <c>RegionRadius + 1</c> — 253 offsets per level, so
    /// 759 regions can be resident at once. That hysteresis band is occupied exactly while the
    /// camera is moving, which is when the pool is under pressure.
    ///
    /// A shell floors at 1024 top quads (every level is 32x32 cells and each cell emits at least a
    /// top quad) = 4096 vertices / 6144 indices = 64 units, and a full sea sheet is the same 64
    /// units, so the degenerate sea-heavy floor is 759 * 128 = 97152 units. Measured per-level peak
    /// shells are 336/285/180 units and a single pathological region can reach 1024 units (AddQuad
    /// refuses past ~65532 vertices). 163840 therefore clears the degenerate floor with 69 %
    /// headroom and leaves exactly one doubling in reserve — the posture
    /// <see cref="TranslucentInitialUnits"/> documents.</summary>
    public const int LodInitialUnits = 163840;

    private readonly GL _gl;
    private readonly GeometryAllocator _allocator;
    private readonly string _name;
    private uint _vbo, _ebo;

    /// <param name="gl">The render thread's GL context.</param>
    /// <param name="initialUnits">Starting capacity in units; the pool doubles from here on demand.</param>
    /// <param name="name">Short label ("opaque", "translucent") used by the growth log line.</param>
    public ChunkGeometryPool(GL gl, int initialUnits, string name)
    {
        _gl = gl;
        _name = name;
        _allocator = new GeometryAllocator(initialUnits);

        Vao = gl.GenVertexArray();
        (_vbo, _ebo) = CreateBuffers(initialUnits);
        RebindVao();
    }

    /// <summary>The pool's vertex array; bound once per pass, never per chunk.</summary>
    public uint Vao { get; }

    /// <summary>Units the backing buffers currently hold (diagnostics).</summary>
    public int CapacityUnits => _allocator.Capacity;

    /// <summary>Units currently handed out to chunks (diagnostics).</summary>
    public int UsedUnits => _allocator.UsedUnits;

    /// <summary>Free blocks in the allocator's list right now (diagnostics; see
    /// <see cref="GeometryAllocator.FreeBlockCount"/>).</summary>
    public int FreeBlockCount => _allocator.FreeBlockCount;

    /// <summary>Lifetime high-water free-block count (diagnostics; see
    /// <see cref="GeometryAllocator.PeakFreeBlockCount"/>).</summary>
    public int PeakFreeBlockCount => _allocator.PeakFreeBlockCount;

    /// <summary>Free-list entries the allocator's scans have examined since construction
    /// (diagnostics; see <see cref="GeometryAllocator.ScanSteps"/>).</summary>
    public long ScanSteps => _allocator.ScanSteps;

    /// <summary>Allocate plus free calls since construction (diagnostics; see
    /// <see cref="GeometryAllocator.AllocatorCalls"/>).</summary>
    public long AllocatorCalls => _allocator.AllocatorCalls;

    /// <summary>
    /// Units a mesh of this size occupies: the larger of the two ceilings, since one unit covers
    /// <see cref="VerticesPerUnit"/> vertices AND <see cref="IndicesPerUnit"/> indices at the same
    /// unit offset. Quad geometry sits exactly on the 4:6 ratio, so the two ceilings agree and
    /// nothing is wasted; anything else rounds up to whichever buffer runs out first. This is the
    /// pool's one piece of arithmetic that needs no GL context, so it is public and pinned by
    /// <c>LodGeometryPoolTests</c> rather than inferred from a running renderer.
    /// </summary>
    public static int UnitsFor(int vertexCount, int indexCount) =>
        Math.Max(CeilDiv(vertexCount, VerticesPerUnit), CeilDiv(indexCount, IndicesPerUnit));

    /// <summary>
    /// Copies one chunk mesh into the pool and returns the slice it landed in. The copy is
    /// synchronous, so the caller may recycle its lists as soon as this returns (which is what
    /// <c>ChunkManager.ApplyMesh</c> does with the <see cref="MeshBufferPool"/> lists).
    /// </summary>
    /// <exception cref="ArgumentException">Both spans are empty; callers must skip empty meshes.</exception>
    public GeometrySlice Upload(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<ushort> indices)
    {
        var units = UnitsFor(vertices.Length, indices.Length);
        if (units <= 0)
            throw new ArgumentException("Refusing to allocate a pool slice for an empty mesh.", nameof(vertices));

        if (!_allocator.TryAllocate(units, out var unit))
        {
            // A grow adds at least `units` contiguous units at the tail, so the retry cannot fail.
            Grow(Math.Max(_allocator.Capacity * 2, _allocator.Capacity + units));
            if (!_allocator.TryAllocate(units, out unit))
                throw new InvalidOperationException(
                    $"Chunk geometry pool '{_name}' could not fit {units} units after growing.");
        }

        // Both writes name their buffer directly, so the upload binds nothing at all: there is no
        // target to route around and nothing can stomp the current VAO's element binding (which a
        // plain ElementArrayBuffer bind would, since a Mesh leaves its own VAO bound after Render).
        // RebindVao stays the one and only legitimate ElementArrayBuffer bind.
        _gl.NamedBufferSubData(_vbo, (nint)VertexBytes(unit), vertices);
        _gl.NamedBufferSubData(_ebo, (nint)IndexBytes(unit), indices);

        return new GeometrySlice(unit, units, vertices.Length, indices.Length);
    }

    /// <summary>Returns a slice to the free list. The GPU bytes are left as they are; nothing reads them.</summary>
    public void Free(in GeometrySlice slice) => _allocator.Free(slice.UnitOffset, slice.UnitCount);

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteVertexArray(Vao);

        GC.SuppressFinalize(this);
    }

    private static int CeilDiv(int value, int divisor) => (value + divisor - 1) / divisor;

    // Byte arithmetic runs in long (Vertex.Size is a uint), so a doubling past 131072 units
    // cannot silently overflow an int before it reaches the GL call.
    private static nuint VertexBytes(long units) => (nuint)(units * VerticesPerUnit * Vertex.Size);

    private static nuint IndexBytes(long units) => (nuint)(units * IndicesPerUnit * sizeof(ushort));

    /// <summary>
    /// Allocates an immutable-size vertex/index buffer pair for <paramref name="units"/> units.
    /// <c>DynamicStorageBit</c> is the only flag needed: it is exactly what permits the
    /// <c>glNamedBufferSubData</c> writes <see cref="Upload"/> does, and the pool is never mapped.
    /// </summary>
    private (uint Vbo, uint Ebo) CreateBuffers(int units)
    {
        var vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.CopyWriteBuffer, vbo);
        _gl.BufferStorage(BufferStorageTarget.CopyWriteBuffer, VertexBytes(units), null,
            BufferStorageMask.DynamicStorageBit);

        var ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.CopyWriteBuffer, ebo);
        _gl.BufferStorage(BufferStorageTarget.CopyWriteBuffer, IndexBytes(units), null,
            BufferStorageMask.DynamicStorageBit);

        _gl.BindBuffer(BufferTargetARB.CopyWriteBuffer, 0);
        return (vbo, ebo);
    }

    /// <summary>
    /// Points the pool's VAO at the current buffer pair. The only place that binds
    /// <c>ElementArrayBuffer</c> — the element binding is VAO state, so it must be captured here
    /// and nowhere else. Re-run after growth, since the buffer names change.
    /// </summary>
    private void RebindVao()
    {
        _gl.BindVertexArray(Vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        Mesh.ConfigureVertexAttribs(_gl);
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        _gl.BindVertexArray(0);
    }

    /// <summary>
    /// Reallocates both buffers larger and copies the old contents across on the GPU, so every
    /// live <see cref="GeometrySlice"/> keeps its offsets and stays valid. Costs one full-buffer
    /// copy, which can hitch a frame; the log line makes that visible in a perf capture.
    /// </summary>
    private void Grow(int newUnits)
    {
        var oldUnits = _allocator.Capacity;
        var (vbo, ebo) = CreateBuffers(newUnits);

        _gl.BindBuffer(BufferTargetARB.CopyReadBuffer, _vbo);
        _gl.BindBuffer(BufferTargetARB.CopyWriteBuffer, vbo);
        _gl.CopyBufferSubData(CopyBufferSubDataTarget.CopyReadBuffer, CopyBufferSubDataTarget.CopyWriteBuffer,
            0, 0, VertexBytes(oldUnits));

        _gl.BindBuffer(BufferTargetARB.CopyReadBuffer, _ebo);
        _gl.BindBuffer(BufferTargetARB.CopyWriteBuffer, ebo);
        _gl.CopyBufferSubData(CopyBufferSubDataTarget.CopyReadBuffer, CopyBufferSubDataTarget.CopyWriteBuffer,
            0, 0, IndexBytes(oldUnits));

        _gl.BindBuffer(BufferTargetARB.CopyReadBuffer, 0);
        _gl.BindBuffer(BufferTargetARB.CopyWriteBuffer, 0);

        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _vbo = vbo;
        _ebo = ebo;

        RebindVao();
        _allocator.Grow(newUnits);

        var megabytes = VertexBytes(newUnits) / (1024 * 1024);
        // Free-list shape on the growth line is what item D5 literally asks for. It fires only on a
        // doubling — OpaqueInitialUnits already sits where the opaque pool settled, and R10 records
        // the lod pool growing in none of 12 perf-fly runs — so this is NOT the capture instrument;
        // the per-second `geo` token on FrameProfiler's console line is.
        Log.WriteLine($"Chunk geometry pool '{_name}' grew to {newUnits} units ({megabytes} MB vertices), " +
                      $"free blocks {_allocator.FreeBlockCount}, largest {_allocator.LargestFreeBlock}");
    }
}
