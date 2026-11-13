using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Chunks.Sources;
using VoxelEngine.Player;
using VoxelEngine.Rendering.Geometry;

namespace VoxelEngine.Lod;

/// <summary>
/// Manages far-terrain LOD regions across all LOD levels: a pool of
/// <see cref="StreamingConfig.MaxConcurrentLodGenerations"/> BelowNormal LodGen threads
/// builds region meshes from the heightmap cache (CPU only), the main thread uploads them into
/// the render manager's shared LOD <see cref="ChunkGeometryPool"/> with a per-frame cap and
/// unloads regions that fall out of range. That constant ships at 1 — C5 measured a wider pool
/// and declined it (the reasoning is on the constant) — but everything here is written for N
/// workers and is correct at any N.
/// Regions are built nearest-first across every level, the finer level first at equal distance, so detail
/// appears near the player before the horizon and a coarse shell under the player (after a long teleport) is
/// replaced by its finer children within the first few builds.
/// Each worker claims a region through <see cref="ClaimNextRegion"/>, which registers the key in
/// <c>LodRegionClaims._building</c>; the claim protocol, not the concurrent collections, is what keeps two
/// workers off one region (item C5).
/// </summary>
public class LodManager
{
    /// <summary>
    /// The shared pool every resident region's terrain shell and sea sheet is uploaded into, or
    /// null headless (the tests and a headless World), where a non-empty build is dropped by
    /// <see cref="ApplyCompletedMeshes"/> instead of uploaded. One pool covers both meshes
    /// because a region's pair is born from one <see cref="LodMeshData"/> and freed together.
    /// Render thread only, like the two chunk pools.
    /// </summary>
    private readonly ChunkGeometryPool? _lodGeometry;

    /// <summary>
    /// Finished region builds <see cref="ApplyCompletedMeshes"/> uploads per frame. Defaults to
    /// <see cref="StreamingConfig.MaxLodUploadsPerFrame"/>; World raises it while a world is
    /// loading and restores it when the world starts playing. Main thread only.
    /// </summary>
    public int UploadBudget { get; set; } = StreamingConfig.MaxLodUploadsPerFrame;

    /// <summary>
    /// Where the far LOD reads its terrain: worldgen by default (<see cref="GeneratedWorldSource"/>),
    /// the world's own <see cref="ChunkManager.WorldSource"/> otherwise (World copies it). Set
    /// before <see cref="Start"/> (the LodGen <c>Thread.Start</c> publishes it) and never changed
    /// afterwards; read on the LodGen workers.
    /// </summary>
    public IWorldSource WorldSource { get; set; } = GeneratedWorldSource.Instance;

    public readonly ConcurrentDictionary<(int Level, int X, int Z), LodRegion> Regions = new();

    /// <summary>
    /// The same regions as <see cref="Regions"/>, flat, so the main thread can walk them
    /// without the locked full-table copy <c>Regions.Values</c> makes every frame. Order is
    /// irrelevant (the render passes sort what they draw), so entries are swap-removed.
    /// Main thread only — <see cref="Update"/> maintains it and the render passes read it.
    /// <see cref="Regions"/> stays concurrent because the background builder probes it by key.
    /// </summary>
    public readonly List<LodRegion> RegionList = new(512);


    /// <summary>
    /// LOD-sized twin of <see cref="ChunkManager.MeshBuffers"/>. The LodGen worker rents a region
    /// build's four output lists here and the main thread hands them back in
    /// <see cref="ApplyCompletedMeshes"/>, after the upload or on the out-of-range drop — a queued
    /// LodMeshData can wait frames, so returning on the worker would hand out a live list.
    /// Water lists come back even when empty (retainEmptyTranslucent): unlike a chunk mesher, a
    /// region build rents them every time, so dropping the empty ones would drain the pool inland.
    /// <para>
    /// The retention ceilings are DERIVED from the in-flight bound, not chosen. Outstanding rents
    /// are capped at <see cref="StreamingConfig.MaxLodBuildsInFlight"/> = 8 (finished builds
    /// waiting in <c>_completedMeshes</c>, each pinning its four lists) plus
    /// <see cref="StreamingConfig.MaxConcurrentLodGenerations"/> (builds in progress), so
    /// the bound is 8 + N. The ceiling is sized for the largest pool C5 measured (N = 3, bound 11)
    /// rather than for the N = 1 the measurement told it to ship, so that raising the constant
    /// stays a one-token experiment: 16 keeps the whole in-flight set recyclable with headroom for
    /// the frame's returns at any N up to 8. Below the bound a steady fraction of returns would be
    /// dropped and re-rented as fresh LOH allocations, inverting the win C8 landed.
    /// The arithmetic, so the next round need not redo it: Vertex is 32 bytes, so one build's
    /// four lists are 24576*32 + 36864*2 + 4096*32 + 6144*2 ~= 1.00 MB; 11 in flight ~= 11 MB,
    /// 16 retained ~= 15.7 MB, never trimmed once the burst has filled the pool. If that idle
    /// retention ever matters the lever is MaxLodBuildsInFlight, not the ceiling.
    /// The return end is still MAIN THREAD ONLY and unchanged by C5 — only the rent end became
    /// MaxConcurrentLodGenerations-wide, and every Rent/Return is locked, so the contract holds at
    /// any N even though the shipped N is 1.
    /// </para>
    /// </summary>
    public MeshBufferPool MeshBuffers { get; } = new(
        vertexCapacity: LodRegion.MeshVertexCapacity,
        indexCapacity: LodRegion.MeshIndexCapacity,
        translucentVertexCapacity: LodRegion.WaterVertexCapacity,
        translucentIndexCapacity: LodRegion.WaterIndexCapacity,
        maxPooled: 16,
        maxPooledTranslucent: 16,
        retainEmptyTranslucent: true);
    private Camera? _camera;
    private readonly LodRegionSet _regionSet;
    private readonly LodCoverPass _coverPass;
    private readonly LodRegionClaims _claims;
    private readonly LodWorkerPool _workers;

    /// <summary>Builds the manager's components. <paramref name="lodGeometry"/> is the shared LOD pool, or null headless (see <c>_lodGeometry</c>).</summary>
    public LodManager(ChunkGeometryPool? lodGeometry = null)
    {
        _lodGeometry = lodGeometry;
        _regionSet = new LodRegionSet(Regions, RegionList);
        _coverPass = new LodCoverPass(_regionSet);
        _claims = new LodRegionClaims(this);
        _workers = new LodWorkerPool(this, _claims);
    }

    /// <summary>Binds the camera the region scan measures distance from. Split out of
    /// <see cref="Start"/> so tests can reach <see cref="FindClosestNeededRegion"/> — which
    /// dereferences the camera through <c>CameraRegion</c> — without spawning the LodGen threads.
    /// </summary>
    internal void AttachCamera(Camera camera) => _camera = camera;

    /// <inheritdoc cref="LodWorkerPool.AnyThreadAlive"/>
    internal bool AnyThreadAlive => _workers.AnyThreadAlive;

    /// <inheritdoc cref="LodWorkerPool.Start"/>
    public void Start(Camera camera) => _workers.Start(camera);

    /// <inheritdoc cref="LodWorkerPool.Stop"/>
    public bool Stop() => _workers.Stop();

    /// <summary>
    /// Unloads every region at once and empties every table this manager keeps, handing each
    /// region's slices back to the shared LOD pool (which stays alive for the next world) and
    /// every waiting build's lists back to <see cref="MeshBuffers"/>. Main thread only (it frees
    /// GL slices).
    /// <para>
    /// PRECONDITION: <see cref="Stop"/> returned true, so no LodGen thread can claim a key or
    /// enqueue a build while this runs. Calling it twice is a no-op the second time.
    /// </para>
    /// </summary>
    public void ReleaseAll()
    {
        _regionSet.ReleaseRegions();

        _claims.ReleaseClaims();

        _regionSet.BumpVersion();
        _coverPass.ResetCover();
    }

    /// <inheritdoc cref="LodRegionClaims.CompletedBuildCount"/>
    public long CompletedBuildCount => _claims.CompletedBuildCount;
    /// <inheritdoc cref="LodRegionClaims.EnqueueCompletedMesh"/>
    internal void EnqueueCompletedMesh(LodMeshData data) => _claims.EnqueueCompletedMesh(data);
    /// <inheritdoc cref="LodRegionClaims.MarkEditedColumn"/>
    public void MarkEditedColumn(int x, int z) => _claims.MarkEditedColumn(x, z);
    /// <inheritdoc cref="LodRegionClaims.TakeDirtyRegion"/>
    internal (int Level, int X, int Z)? TakeDirtyRegion() => _claims.TakeDirtyRegion();
    /// <inheritdoc cref="LodRegionClaims.FindClosestNeededRegion"/>
    internal (int Level, int X, int Z)? FindClosestNeededRegion() => _claims.FindClosestNeededRegion();
    /// <inheritdoc cref="LodRegionClaims.ClaimNextRegion"/>
    internal (int Level, int X, int Z)? ClaimNextRegion() => _claims.ClaimNextRegion();
    /// <inheritdoc cref="LodRegionClaims.ReleaseRegion"/>
    internal void ReleaseRegion((int Level, int X, int Z) key) => _claims.ReleaseRegion(key);
    /// <inheritdoc cref="LodRegionClaims.PendingUploadCount"/>
    internal int PendingUploadCount => _claims.PendingUploadCount;
    /// <inheritdoc cref="LodRegionClaims.TryTakeCompletedMesh"/>
    internal bool TryTakeCompletedMesh(out LodMeshData data) => _claims.TryTakeCompletedMesh(out data);
    /// <inheritdoc cref="LodRegionClaims.QueuedClaimCount"/>
    internal int QueuedClaimCount => _claims.QueuedClaimCount;
    /// <inheritdoc cref="LodRegionClaims.BuildingClaimCount"/>
    internal int BuildingClaimCount => _claims.BuildingClaimCount;
    /// <inheritdoc cref="LodRegionClaims.TryBuildOneRegion"/>
    internal LodBuildOutcome TryBuildOneRegion() => _claims.TryBuildOneRegion();


    internal (int X, int Z) CameraRegion(int level)
    {
        var pos = _camera!.Position;
        var size = LodRegion.Levels[level].RegionBlocks;
        return (MathUtils.FloorToInt(pos.X / size), MathUtils.FloorToInt(pos.Z / size));
    }

    /// <summary>The camera's horizontal position in blocks (x, z), which the claim scan measures region distance from.</summary>
    internal Vector2 CameraXZ => new(_camera!.Position.X, _camera.Position.Z);

    public void Update(ChunkManager chunkManager)
    {
        if (_camera == null) return;

        var camChunk = (_camera.Position / Chunk.Size).Floor();
        Span<(int camX, int camZ)> camRegions = stackalloc (int, int)[LodRegion.Levels.Length];
        for (var level = 0; level < LodRegion.Levels.Length; level++)
            camRegions[level] = CameraRegion(level);

        // Backwards by index because this loop removes as it goes: a swap-remove drops the
        // last entry into the hole, and a descending walk has already visited that entry.
        for (var i = RegionList.Count - 1; i >= 0; i--)
        {
            var region = RegionList[i];
            var radius = LodRegion.Levels[region.Level].RegionRadius + 1;
            var dx = region.X - camRegions[region.Level].camX;
            var dz = region.Z - camRegions[region.Level].camZ;
            // Out of its own ring, or deep enough inside the finer one that its children replace it
            if (dx * dx + dz * dz <= radius * radius
                && !(region.Level > 0 && LodRegion.IsSupersededByFinerRing(region.Level, region.X, region.Z,
                    camRegions[region.Level - 1], StreamingConfig.LodSupersededDropMarginRegions)))
                continue;

            var key = (region.Level, region.X, region.Z);
            if (!Regions.TryRemove(key, out var removed)) continue;
            RemoveRegion(removed);
            removed.Dispose();
            _claims.Unqueue(key);
        }

        ApplyCompletedMeshes(camRegions);

        // The flags depend only on which chunks are loaded and meshed (MeshVersion), which regions
        // exist (_regionSetVersion) and the camera chunk (the distance early-out in
        // IsCoveredByChunks), so the pass is skipped while none of them moved. A region added above
        // starts with IsBuried == false; the add bumps the region-set version, so the pass always runs
        // in the frame a region appears and never draws a shell for a frame it should not.
        UpdateCoverFlags(chunkManager, camChunk);
    }

    /// <summary>
    /// Uploads finished region builds to the GPU, capped per frame, and drops the ones that left
    /// the camera's range while they waited. Owns the returned end of the rent/return contract on
    /// <see cref="MeshBuffers"/>: every dequeued <see cref="LodMeshData"/> hands its four lists
    /// back exactly once here, on both exits, and nothing reads them afterwards. Main thread only
    /// (GL); internal so tests can drive both paths without a worker thread.
    /// </summary>
    internal void ApplyCompletedMeshes(ReadOnlySpan<(int camX, int camZ)> camRegions)
    {
        var uploaded = 0;
        while (uploaded < UploadBudget && _claims.TryTakeCompletedMesh(out var data))
        {
            var key = (data.Level, data.X, data.Z);
            var radius = LodRegion.Levels[data.Level].RegionRadius + 1;
            var dx = data.X - camRegions[data.Level].camX;
            var dz = data.Z - camRegions[data.Level].camZ;

            // Moved out of range while queued; allow it to regenerate later
            if (dx * dx + dz * dz > radius * radius)
            {
                _claims.Unqueue(key);
                // Nothing was ever built from this data, so its lists recycle straight away.
                MeshBuffers.Return(data.Vertices, data.Indices, data.WaterVertices, data.WaterIndices);
                continue;
            }

            // Headless (no pool): a real build has nowhere to go, so it is dropped as if uploaded.
            // The key stays in _queued so the builder does not spin on it. The LodRegion
            // constructor's throw for this case was unreachable in every headless test, and this
            // is what lets a headless World drive Update over real terrain.
            if (_lodGeometry == null && (data.Vertices.Count > 0 || data.WaterVertices.Count > 0))
            {
                MeshBuffers.Return(data.Vertices, data.Indices, data.WaterVertices, data.WaterIndices);
                uploaded++;
                continue;
            }

            // A rebuild frees the region it replaces BEFORE uploading the new one, so a
            // same-sized rebuild lands back in the exact hole it just vacated: the pool's free
            // list comes out of a rebuild unchanged instead of gaining a fragment, and the two
            // copies never occupy the pool at the same time. The fade state is the only thing
            // the new region needs from the old one, so it is carried across by hand.
            //
            // The order does change one failure path, deliberately. LodRegion's constructor can
            // throw out of ChunkGeometryPool.Upload, whose single reachable throw is "could not
            // fit N units after growing"; after this reorder the replaced region is already out
            // of Regions and RegionList when that happens, with nothing scheduled to rebuild it,
            // where the old order left the stale region standing. Accepted: that throw means the
            // pool could neither fit the slice nor grow to hold it, which is unrecoverable and
            // already fatal in practice.
            var carriedLifetime = 0.0;
            if (Regions.TryRemove(key, out var replaced))
            {
                carriedLifetime = replaced.Lifetime; // rebuilt region keeps its fade state
                RemoveRegion(replaced);
                replaced.Dispose();
            }

            var region = new LodRegion(_lodGeometry, data) { Lifetime = carriedLifetime };
            // ChunkGeometryPool.Upload copies synchronously (glNamedBufferSubData), so the lists
            // can be recycled at once.
            MeshBuffers.Return(data.Vertices, data.Indices, data.WaterVertices, data.WaterIndices);

            AddRegion(key, region);
            uploaded++;
        }
    }

    /// <inheritdoc cref="LodCoverPass.UpdateCoverFlags"/>
    internal void UpdateCoverFlags(ChunkManager chunkManager, Vector3 camChunk) => _coverPass.UpdateCoverFlags(chunkManager, camChunk);

    /// <summary>
    /// Binds the camera without starting any LodGen worker: a multiplayer client's regions arrive
    /// over the network (<see cref="AddStreamedRegion"/>), so it needs the removal/cover passes of
    /// <see cref="Update"/> but never builds anything itself.
    /// </summary>
    public void BindCamera(Camera camera) => _camera = camera;

    /// <summary>
    /// Adds a far-LOD region built by the server, uploading its mesh to this client's LOD pool and
    /// handing it to the same lifetime/burial machinery as a locally built one. A key already
    /// present is left alone (a rebuild the client did not ask for is stale).
    /// </summary>
    public void AddStreamedRegion(LodMeshData data)
    {
        var key = (data.Level, data.X, data.Z);
        if (Regions.ContainsKey(key))
            return;
        AddRegion(key, new LodRegion(_lodGeometry, data));
    }

    /// <inheritdoc cref="LodRegionSet.AddRegion"/>
    internal void AddRegion((int Level, int X, int Z) key, LodRegion region) => _regionSet.AddRegion(key, region);

    /// <inheritdoc cref="LodRegionSet.RemoveRegion"/>
    internal void RemoveRegion(LodRegion region) => _regionSet.RemoveRegion(region);
}
