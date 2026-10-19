using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Chunks;
using VoxelEngine.Lod;
using VoxelEngine.Player;
using VoxelEngine.Rendering.Geometry;

namespace VoxelEngine.Rendering;

/// <summary>
/// The terrain passes: the far LOD regions, the opaque chunks and, after the entities, the
/// translucent chunks and LOD water sheets, each culled and sorted on the CPU and issued as one
/// indirect draw of the CHUNK_DRAW world program. <see cref="CullWater"/> also decides once a
/// frame whether any water is on screen, which gates the water mirror and the scene copies.
/// The programs, the geometry pools and the <see cref="ChunkDrawBatch"/> stay owned by
/// <see cref="RenderManager"/>, and the water mirror shares this class's scratch lists.
/// Render thread only.
/// </summary>
internal sealed class TerrainPasses
{
    private readonly GL _gl;
    private readonly RenderManager _owner;

    /// <summary>Chunks the last <see cref="RenderWorld"/> call passed the frustum test.</summary>
    internal int LastVisibleChunkCount { get; private set; }

    // Live views of the scratch lists and water flags, for the water mirror, which shares them.
    internal List<(Chunk chunk, float distSq)> VisibleChunks => _visibleChunks;
    internal List<(LodRegion region, float distSq)> VisibleRegions => _visibleRegions;
    internal bool WaterVisible => _waterVisible;
    internal bool NearWaterVisible => _nearWaterVisible;

    private RenderTargets Targets => _owner.Targets;

    internal TerrainPasses(GL gl, RenderManager owner)
    {
        _gl = gl;
        _owner = owner;
    }

    // Wall-clock timestamp of the previous RenderWorld, so the chunk fade-in can advance by
    // real delta time instead of an assumed 60 Hz step (PERF_TODO item 10).
    private double _lastWorldTime;

    // Reused across frames to avoid a per-frame allocation
    private readonly List<(Chunk chunk, float distSq)> _visibleChunks = new(1024);

    /// <summary>
    /// Draws the opaque world. <paramref name="chunks"/> is
    /// <see cref="ChunkManager.Renderable"/>: every chunk with something to draw, opaque or
    /// translucent — the water-only ones still need their fade lifetime advanced here.
    /// The surviving chunks are frustum-culled and sorted front to back on the CPU exactly as
    /// before, but they are then appended to <see cref="ChunkDrawBatch"/> and issued as one
    /// <c>glMultiDrawElementsIndirect</c> out of the opaque <see cref="ChunkGeometryPool"/>.
    /// </summary>
    internal void RenderWorld(Camera camera, IReadOnlyList<Chunk> chunks, double time)
    {
        if (camera == null) return;

        // time is _window.Time; the first frame has no previous stamp, so it falls back to one
        // 60 Hz step. The clamp keeps a hitch (or a debugger break) from advancing every
        // streaming chunk straight to full alpha.
        var dt = _lastWorldTime > 0 ? MathUtils.Clamp(time - _lastWorldTime, 0.0, 0.1) : 1.0 / 60.0;
        _lastWorldTime = time;

        _owner.ChunkShader.Use();
        _owner.BindWorldTextures();

        var viewProj = camera.GetViewProjection();
        _owner.ChunkShader.SetUniform(_owner.LocChunkViewProj, viewProj);
        var frustum = camera.GetViewFrustum();
        var camChunkPos = (camera.RenderEye / Chunk.Size).Floor();

        // Sort chunks by distance for better cache coherency and early z-rejection
        _visibleChunks.Clear();

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];

            // Renderable holds only chunks with something to draw, but a water-only ocean
            // chunk has no opaque mesh — it is here to have its lifetime advanced, or its
            // fluid mesh would stay at alpha 0 forever.
            if (!chunk.HasMesh && !chunk.HasTranslucentMesh)
                continue;

            if (!frustum.IntersectsAABB(chunk.WorldMin, chunk.WorldMax))
                continue;

            var distSq = Vector3.DistanceSquared(chunk.Position, camChunkPos);
            _visibleChunks.Add((chunk, distSq));
        }

        // Sort by distance (front to back for better z-buffer performance)
        _visibleChunks.Sort((a, b) => a.distSq.CompareTo(b.distSq));

        LastVisibleChunkCount = _visibleChunks.Count;

        _owner.ChunkDraws.BeginPass(_visibleChunks.Count);

        foreach (var (chunk, _) in _visibleChunks)
        {
            // Real delta time: the fade (Chunk.MAX_LIFETIME, 0.5 s) now takes 0.5 s of wall
            // clock at any frame rate instead of 30 frames.
            chunk.lifetime += dt;
            // Water-only chunks are in the list purely to have their lifetime advanced
            if (chunk.OpaqueSlice is not { } slice) continue;

            _owner.ChunkDraws.Add(slice, chunk.WorldMin,
                MathF.Min(1F, (float)(chunk.lifetime / Chunk.MAX_LIFETIME)));
        }

        _owner.ChunkDraws.Draw(_owner.OpaqueChunkGeometry.Vao, _owner.ChunkShader, _owner.LocChunkDrawBase);
    }

    // Reused across frames, like _visibleChunks / _visibleRegions
    private readonly List<(Chunk chunk, float distSq)> _translucentChunks = new(256);
    private readonly List<(LodRegion region, float distSq)> _translucentRegions = new(256);

    // Whether anything the translucent pass will draw is actually water, recomputed by
    // CullWater once a frame. Glass alone does not turn the water-only passes back on.
    private bool _waterVisible;
    // Whether any of that water is near enough for the fancy model (nearer than
    // WaterFancyFarBlocks). Far-only water draws through the cheap shader path, which
    // samples neither the reflection nor the scene copies, so both passes are skipped.
    private bool _nearWaterVisible;

    /// <summary>Frustum-culls the translucent draws once per frame and records whether any water
    /// (chunk or LOD sheet) is on screen. Reflection and the scene copies only run when it is,
    /// and RenderWorldTranslucent draws from the lists filled here. Glass-only chunks are listed
    /// for drawing but never trigger the water-only passes.
    /// Takes the translucent subset (<see cref="ChunkManager.TranslucentRenderable"/>), not all
    /// meshed chunks.</summary>
    internal void CullWater(Camera camera, IReadOnlyList<Chunk> chunks, IReadOnlyList<LodRegion> regions)
    {
        _translucentChunks.Clear();
        _translucentRegions.Clear();
        _waterVisible = false;
        _nearWaterVisible = false;
        if (camera == null) return;

        var frustum = camera.GetViewFrustum();
        var camChunkPos = (camera.RenderEye / Chunk.Size).Floor();
        var camPos = camera.RenderEye;
        var fancyFarSq = WaterRenderConfig.WaterFancyFarBlocks * WaterRenderConfig.WaterFancyFarBlocks;

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            if (!chunk.HasTranslucentMesh)
                continue;

            if (!frustum.IntersectsAABB(chunk.WorldMin, chunk.WorldMax))
                continue;

            _translucentChunks.Add((chunk, Vector3.DistanceSquared(chunk.Position, camChunkPos)));
            if (chunk.HasWaterMesh)
            {
                _waterVisible = true;
                var center = chunk.WorldMin + new Vector3(Chunk.Size / 2f);
                if (Vector3.DistanceSquared(center, camPos) <= fancyFarSq)
                    _nearWaterVisible = true;
            }
        }

        // Back to front, so overlapping surfaces (a pond seen across a sea) blend in the
        // order the eye sees them
        _translucentChunks.Sort((a, b) => b.distSq.CompareTo(a.distSq));

        for (var i = 0; i < regions.Count; i++)
        {
            var region = regions[i];
            if (!region.HasWaterMesh || region.IsBuried)
                continue;

            var min = new Vector3(region.X * region.SizeBlocks, region.YMin, region.Z * region.SizeBlocks);
            var max = new Vector3(min.X + region.SizeBlocks, region.YMax, min.Z + region.SizeBlocks);

            if (!frustum.IntersectsAABB(min, max))
                continue;

            var center = (min + max) * 0.5f;
            var regionDistSq = Vector3.DistanceSquared(center, camPos);
            _translucentRegions.Add((region, regionDistSq));
            // A LOD region's translucent mesh is only ever the water sheet
            _waterVisible = true;
            if (regionDistSq <= fancyFarSq)
                _nearWaterVisible = true;
        }

        // Finest level first, then back to front within a level. The rationale lives on
        // LodRegion.CompareWaterDraw, which is pinned headlessly (LodDrawOrderTests); since R10
        // this ordering is also the glMultiDrawElementsIndirect submission order.
        _translucentRegions.Sort((a, b) =>
            LodRegion.CompareWaterDraw(a.region.Level, a.distSq, b.region.Level, b.distSq));
    }

    /// <summary>
    /// Draws every fluid surface — chunk water first, then the far LOD's water sheets — in one
    /// blended pass after the whole opaque world, so terrain, drops and TNT behind the water
    /// show through it. Depth writing stays on: water bodies are at most a couple of layers
    /// deep from any viewpoint, and writing depth is what makes the chunk-vs-LOD overlap at
    /// the full-detail ring resolve by distance instead of by draw order.
    /// The lists it walks were filled by <see cref="CullWater"/> earlier in the frame, which
    /// also decided whether the scene copy below is worth taking.
    /// </summary>
    internal void RenderWorldTranslucent(Camera camera)
    {
        if (camera == null) return;

        // The refraction reads the frame as it stands right now: everything opaque plus the
        // entities, and nothing translucent yet. Depth comes along for the thickness measure.
        // Skipped for far-only water: the cheap shader path samples neither copy.
        if (_nearWaterVisible) Targets.CopyScene(color: true);

        _owner.ChunkShader.Use();
        // No cutout here: glass's faint interior must blend instead of discarding away.
        _owner.ChunkShader.SetUniform(_owner.LocChunkAlphaCutoff, 0f);
        _owner.BindWorldTextures();
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(true);

        var viewProj = camera.GetViewProjection();
        _owner.ChunkShader.SetUniform(_owner.LocChunkViewProj, viewProj);

        _owner.ChunkDraws.BeginPass(_translucentChunks.Count);

        foreach (var (chunk, _) in _translucentChunks)
            _owner.ChunkDraws.Add(chunk.TranslucentSlice!.Value, chunk.WorldMin,
                MathF.Min(1f, (float)(chunk.lifetime / Chunk.MAX_LIFETIME)));

        _owner.ChunkDraws.Draw(_owner.TranslucentChunkGeometry.Vao, _owner.ChunkShader, _owner.LocChunkDrawBase);

        // The LOD water sheets ride the same program, the same ring and the same cutout-free
        // state the chunk water just used; only the pool VAO differs. Chunk translucent first,
        // LOD water second, exactly as before.
        _owner.ChunkDraws.BeginPass(_translucentRegions.Count);

        foreach (var (region, _) in _translucentRegions)
            _owner.ChunkDraws.Add(region.WaterSlice!.Value,
                new Vector3(region.X * region.SizeBlocks, 0f, region.Z * region.SizeBlocks),
                MathF.Min(1f, (float)(region.Lifetime / LodRegion.MAX_LIFETIME)));

        _owner.ChunkDraws.Draw(_owner.LodGeometry.Vao, _owner.ChunkShader, _owner.LocChunkDrawBase);

        // Later chunk passes reuse this program and need the cutout behavior back.
        _owner.ChunkShader.SetUniform(_owner.LocChunkAlphaCutoff, LightingConfig.AlphaCutoff);
    }

    private readonly List<(LodRegion region, float distSq)> _visibleRegions = new(256);

    /// <summary>
    /// Draws far-terrain LOD regions. Called before RenderWorld so full-detail chunks
    /// draw over the LOD wherever both exist (the LOD sits strictly below the real surface).
    /// <paramref name="regions"/> is <see cref="LodManager.RegionList"/>, every loaded region;
    /// the buried and mesh-less ones are skipped below.
    /// </summary>
    internal void RenderLod(Camera camera, IReadOnlyList<LodRegion> regions)
    {
        if (camera == null) return;

        _owner.ChunkShader.Use();
        _owner.BindWorldTextures();

        var viewProj = camera.GetViewProjection();
        _owner.ChunkShader.SetUniform(_owner.LocChunkViewProj, viewProj);
        var frustum = camera.GetViewFrustum();
        var camPos = camera.RenderEye;

        _visibleRegions.Clear();

        for (var i = 0; i < regions.Count; i++)
        {
            var region = regions[i];

            // Buried regions are fully covered by a finer level (or, for the finest level, by
            // drawn chunks); drawing them too would stack shells visible from inside caves
            if (!region.HasMesh || region.IsBuried)
                continue;

            var min = new Vector3(region.X * region.SizeBlocks, region.YMin, region.Z * region.SizeBlocks);
            var max = new Vector3(min.X + region.SizeBlocks, region.YMax, min.Z + region.SizeBlocks);

            if (!frustum.IntersectsAABB(min, max))
                continue;

            var center = (min + max) * 0.5f;
            _visibleRegions.Add((region, Vector3.DistanceSquared(center, camPos)));
        }

        // Coarsest level first, then front to back within a level. The rationale lives on
        // LodRegion.CompareOpaqueDraw, which is pinned headlessly (LodDrawOrderTests); since R10
        // this ordering is also the glMultiDrawElementsIndirect submission order.
        _visibleRegions.Sort((a, b) =>
            LodRegion.CompareOpaqueDraw(a.region.Level, a.distSq, b.region.Level, b.distSq));

        _owner.ChunkDraws.BeginPass(_visibleRegions.Count);

        foreach (var (region, _) in _visibleRegions)
        {
            region.Lifetime += 1.0 / 60.0;

            _owner.ChunkDraws.Add(region.OpaqueSlice!.Value,
                new Vector3(region.X * region.SizeBlocks, 0f, region.Z * region.SizeBlocks),
                MathF.Min(1f, (float)(region.Lifetime / LodRegion.MAX_LIFETIME)));
        }

        // chunkShader's uAlphaCutoff is already 0.5 (set once at construction and restored by
        // RenderManager.BeginFrame), which is exactly what worldShader carried here.
        _owner.ChunkDraws.Draw(_owner.LodGeometry.Vao, _owner.ChunkShader, _owner.LocChunkDrawBase);
    }

    /// <summary>Drops the per-frame chunk and region draw lists, which still point into the
    /// torn-down world. Called by <see cref="RenderManager.ForgetWorld"/>.</summary>
    internal void ForgetWorld()
    {
        _visibleChunks.Clear();
        _translucentChunks.Clear();
        _visibleRegions.Clear();
        _translucentRegions.Clear();
    }
}
