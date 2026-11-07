using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using VoxelEngine.Chunks;
using VoxelEngine.Data;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Goblins;
using VoxelEngine.Entities.Icebergs;
using VoxelEngine.Entities.Slimes;
using VoxelEngine.Lod;
using VoxelEngine.Player;
using VoxelEngine.Rendering.Actors;
using VoxelEngine.Rendering.Atmosphere;
using VoxelEngine.Simulation;
using VoxelEngine.UI;

namespace VoxelEngine.Rendering;

/// <summary>
/// The planar water mirror: the opaque world, the nearby entities, the sky and the clouds drawn
/// from a camera mirrored across the sea plane into a half-res reflection target, which the
/// water then samples projectively. Owns that target (recreated with the viewport and the
/// reflection-quality setting) and the temporal reuse that skips a re-render while neither the
/// camera nor a reflected entity moved. The world programs, the chunk draw batch and the scratch
/// chunk and region lists it shares with <see cref="TerrainPasses"/> stay owned elsewhere.
/// Render thread only.
/// </summary>
internal sealed class ReflectionPass
{
    private readonly GL _gl;
    private readonly RenderManager _owner;

    // Planar water reflections: the opaque world mirrored across the sea surface, drawn
    // once per frame at WaterReflectionScale resolution. Sampled projectively by water
    // fragments; the depth texture is a render target only, never sampled.
    private uint _reflFbo;
    private uint _reflTex;
    private uint _reflDepthTex;
    private Vector2D<int> _reflSize;
    private Matrix4x4 _reflViewProj = Matrix4x4.Identity;

    // Temporal reuse: the mirror is re-rendered at most every other frame. These describe the
    // camera that actually produced the current _reflTex, so a frame that skips the render can
    // re-push the matrix that matches the texture instead of this frame's.
    private ReflectionReuse.ReflectionPose _reflPose;
    // A real mirror render happened and _reflTex still matches _reflPose.
    private bool _reflMirrorValid;
    private bool _reflReusedLastFrame;

    // Entity positions (fixed order: drops, thrown, primed, falling, slimes, goblins, toppling) at the
    // last real mirror render, and this frame's scratch; swapped when a render happens. A
    // moving entity forces a refresh, since a reused mirror would show it frozen.
    private List<Vector3> _reflEntityPositions = new(64);
    private List<Vector3> _reflEntityScratch = new(64);
    // The entities this frame's mirror draws: near the eye and above the plane.
    private readonly List<ItemDrop> _mirrorDrops = new(32);
    private readonly List<ThrownProjectile> _mirrorThrown = new(8);
    private readonly List<PrimedTnt> _mirrorPrimed = new(8);
    private readonly List<FallingBlock> _mirrorFalling = new(8);
    private readonly List<Slime> _mirrorSlimes = new(16);
    private readonly List<Goblin> _mirrorGoblins = new(8);
    private readonly List<Iceberg> _mirrorIcebergs = new(8);
    private readonly List<TopplingTree> _mirrorToppling = new(4);

    /// <summary>The reflection color texture the world draws bind on unit 2.</summary>
    internal uint ReflTex => _reflTex;

    private FrameLighting Lighting => _owner.Lighting;
    private TextureUnits Units => _owner.Units;
    private RenderTargets Targets => _owner.Targets;
    private SkyPasses Sky => _owner.Sky;
    private TerrainPasses Terrain => _owner.Terrain;
    private ObjectPasses Objects => _owner.Objects;
    private MobPasses Mobs => _owner.Mobs;
    private PlayerPasses Players => _owner.Players;

    internal ReflectionPass(GL gl, RenderManager owner)
    {
        _gl = gl;
        _owner = owner;
    }

    /// <summary>
    /// Builds the half-res reflection texture: a color target plus a depth texture, sized
    /// from the viewport. Called from <see cref="RenderManager.LoadResources"/> and
    /// <see cref="RenderManager.SetViewport"/>; recreating only when the size actually changed.
    /// Returns true when the target was actually (re)created, which also invalidates the
    /// temporal-reuse cache in <see cref="RenderReflection"/> - the fresh color texture holds
    /// nothing a water fragment may sample.
    /// It runs every frame from <see cref="RenderReflection"/>, including frames that then take
    /// that method's early return, so its recreate path must restore the HDR scene target, never
    /// framebuffer 0: a quality-slider change on a frame with no water in view would otherwise
    /// leave the rest of the frame drawing outside the target the post pass resolves.
    /// </summary>
    internal bool CreateReflectionTarget()
    {
        var scale = GameSettings.ReflectionScales[
            Math.Clamp(GameSettings.ReflectionIndex, 0, GameSettings.ReflectionScales.Length - 1)];
        var w = Math.Max(2, (int)(Targets.ViewportSize.X * scale));
        var h = Math.Max(2, (int)(Targets.ViewportSize.Y * scale));
        if (_reflFbo != 0 && _reflSize.X == w && _reflSize.Y == h) return false;

        DeleteReflectionTarget();
        // The new color texture is TexImage2D'd with null data below, so its contents are
        // undefined: the cached mirror is gone and the next eligible frame must render one.
        // Clearing the flag here (rather than relying on the return value) is what covers the
        // RenderManager.SetViewport call site, whose recreation happens outside RenderReflection
        // entirely.
        _reflMirrorValid = false;
        _reflSize = new Vector2D<int>(w, h);

        _reflTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _reflTex);
        unsafe
        {
            // Half-float: the mirror carries the same linear HDR values as the scene target,
            // which 8 bits would band in the darks and clip in the highlights.
            _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba16f,
                (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.Float, null);
        }
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        _reflDepthTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _reflDepthTex);
        unsafe
        {
            _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.DepthComponent24,
                (uint)w, (uint)h, 0, PixelFormat.DepthComponent, PixelType.Float, null);
        }
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);

        _reflFbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _reflFbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _reflTex, 0);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D, _reflDepthTex, 0);

        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            throw new Exception($"Reflection framebuffer incomplete: {status}");

        // Back to the scene target (see the summary: this can run on a frame that renders no mirror).
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, Targets.HdrFbo);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        // Recreate path only. The size-unchanged early return above binds nothing, and this
        // method runs every frame - invalidating up there would cost a full rebind per frame.
        Units.InvalidateTextureUnits();
        return true;
    }

    internal void DeleteReflectionTarget()
    {
        if (_reflFbo == 0) return;
        _gl.DeleteFramebuffer(_reflFbo);
        _gl.DeleteTexture(_reflTex);
        _gl.DeleteTexture(_reflDepthTex);
        _reflFbo = 0;
        // Deleting a texture reverts its bindings to 0 on every unit of the current context.
        Units.InvalidateTextureUnits();
    }

    /// <summary>
    /// Renders the opaque world (chunks + LOD, no water, no entities) from a camera
    /// mirrored across the sea surface into the reflection texture, which the water in
    /// <see cref="TerrainPasses.RenderWorldTranslucent"/> then samples projectively. Below-plane
    /// geometry is clipped via <c>uClipY</c> so the seabed never shows up in the mirror.
    /// Skipped (leaving <c>uReflValid</c> at 0) when there is no water to reflect in, or
    /// when the eye is below the sea plane or submerged in any water
    /// (<see cref="ReflectionReuse.MirrorApplies"/>). Runs after <see cref="ShadowPass.RenderShadows"/> so the
    /// shadow map it samples is already filled.
    /// The mirror is re-rendered at most every other frame: a reused frame issues no GL work
    /// at all and simply re-pushes the cached <c>_reflViewProj</c> that produced the texture,
    /// with the refresh decision in <see cref="ReflectionReuse.ShouldRefresh"/> and its
    /// thresholds in <see cref="WaterRenderConfig"/>.
    /// </summary>
    internal void RenderReflection(Camera camera, IReadOnlyList<Chunk> chunks,
        IReadOnlyList<LodRegion> regions, in ReflectionEntities entities)
    {
        _owner.WorldShader.Use();
        // The target follows the reflection-quality setting: recreating is a no-op while the
        // size is unchanged, so this can run every frame and pick up slider changes live.
        var targetRecreated = CreateReflectionTarget();
        // TerrainPasses.CullWater ran a moment ago, so a frame with no water on screen skips the
        // whole mirrored render rather than filling a texture nothing will sample. Far-only water
        // draws through the cheap shader path, which never samples the mirror either.
        // Named so the SSR mode below can tell the reasons apart; the return condition is the
        // same short-circuit OR it always was.
        var reflectionsOff = !WorldGenerationConfig.WaterEnabled || !GameSettings.WaterReflections;
        var nothingToReflect = camera == null || !Terrain.WaterVisible || !Terrain.NearWaterVisible;
        var mirrorInvalid = !reflectionsOff && !nothingToReflect
            && !ReflectionReuse.MirrorApplies(camera!.RenderEye.Y, RenderManager.WaterPlaneY, camera.IsEyeInWater);
        if (reflectionsOff || nothingToReflect || mirrorInvalid || _reflFbo == 0)
        {
            // SSR follows the reflection setting (off costs nothing), and takes over entirely
            // where the mirror is wrong for eye reasons; with no near water or no target the
            // fancy water branch would not reach it anyway.
            _owner.SetWorldUniform("uWaterSsrMode", mirrorInvalid ? 2f : 0f);
            // A frame that fell through here must never be reused from: however old the
            // texture is, it no longer stands in any known relation to the current camera.
            _reflMirrorValid = false;
            _reflReusedLastFrame = false;
            _reflEntityPositions.Clear();
            _owner.SetWorldUniform("uReflValid", 0f);
            return;
        }

        // Pitch stays within ±89° through the mirror, so UnitY never parallels the view direction.
        var fwd = camera.ViewDirection;
        var pose = new ReflectionReuse.ReflectionPose(camera.RenderEye, fwd, camera.CurrentFov,
            Targets.ViewportSize.X / (float)Math.Max(1, Targets.ViewportSize.Y), RenderManager.WaterPlaneY);
        GatherMirrorEntities(camera.RenderEye, entities);
        var entitiesMoved = ReflectionReuse.EntitiesMoved(CollectionsMarshal.AsSpan(_reflEntityPositions),
            CollectionsMarshal.AsSpan(_reflEntityScratch), WaterRenderConfig.ReflectionReuseMaxEntityMoveBlocks);
        if (!ReflectionReuse.ShouldRefresh(_reflPose, pose, _reflMirrorValid, targetRecreated,
                _reflReusedLastFrame, entitiesMoved))
        {
            _reflReusedLastFrame = true;
            // _reflTex still holds the mirror built from _reflPose, so the water has to project
            // through the matrix that produced it - this frame's would make the mirror swim.
            _owner.SetWorldUniform("uReflViewProj", _reflViewProj);
            _owner.SetWorldUniform("uReflValid", 1f);
            _owner.SetWorldUniform("uWaterSsrMode", 1f);
            return;
        }

        // The reflection texture is this pass's render target, so it must not stay bound
        // to unit 2 while it draws: sampling and rendering the same texture is a
        // feedback loop (see ShadowPass.RenderShadows for unit 1). Later passes rebind it.
        Units.BindUnit(2, TextureTarget.Texture2D, 0);
        Units.ActiveUnit(0);

        // Mirror position and facing across the horizontal plane.
        var view = ReflectionCull.MirrorView(camera.RenderEye, fwd, RenderManager.WaterPlaneY,
            out var mirroredPos, out var mirroredFwd);
        _reflViewProj = view * camera.GetProjectionMatrix();

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _reflFbo);
        _gl.Viewport(_reflSize);
        _gl.ClearColor(Lighting.SkyColor.X, Lighting.SkyColor.Y, Lighting.SkyColor.Z, 1f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        // The sky dome and the sun belong in the mirror for the same reason they do in the real
        // frame: they are painted before any terrain, and everything after occludes them. The
        // mirror's own look-at has no roll, and neither does its world view, so they agree.
        var mirrorRot = view;
        mirrorRot.M41 = mirrorRot.M42 = mirrorRot.M43 = 0f;
        // The stars are a function of direction alone, so the mirrored dome reflects them where
        // they belong; only the cloud cover in front of them is measured from the mirrored eye,
        // as the mirror's clouds are drawn from it. The lens glare is the camera's, not the
        // sky's, so the mirror never reflects it.
        Sky.RenderSky(mirrorRot, camera.GetProjectionMatrix(), mirroredFwd,
            Lighting.SkyFrame with { Eye = mirroredPos, GlareColor = Vector3.Zero });
        // SkyPasses.RenderSun leaves its own program and its disc on unit 0; the mirrored world draws
        // below want ours and the atlas back.
        _owner.WorldShader.Use();
        Units.BindUnit(0, TextureTarget.Texture2DArray, _owner.AtlasTexture);
        _gl.Disable(EnableCap.Blend);

        // Fog and specular inside the texture want the mirrored eye; the clip drops
        // everything below the surface. Both are restored below.
        var savedCamPos = Lighting.CameraPos;
        var savedClip = _owner.ClipY;
        Lighting.CameraPos = mirroredPos;
        _owner.ClipY = RenderManager.WaterPlaneY;
        _owner.ApplyFrameUniforms();
        _owner.SetWorldUniform("uReflViewProj", _reflViewProj);

        var frustum = new Frustum(_reflViewProj);

        Terrain.VisibleChunks.Clear();
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            if (!chunk.HasMesh) continue;
            // The mirror clips below RenderManager.WaterPlaneY, so geometry wholly under it draws no pixel.
            if (!ReflectionCull.MayReachPlane(chunk.WorldMax.Y)) continue;

            if (!frustum.IntersectsAABB(chunk.WorldMin, chunk.WorldMax)) continue;

            Terrain.VisibleChunks.Add((chunk, Vector3.DistanceSquared(chunk.WorldMin, mirroredPos)));
        }

        Terrain.VisibleChunks.Sort((a, b) => a.distSq.CompareTo(b.distSq));

        _owner.ChunkShader.Use();
        _owner.ChunkShader.SetUniform(_owner.LocChunkViewProj, _reflViewProj);
        _owner.ChunkDraws.BeginPass(Terrain.VisibleChunks.Count);

        foreach (var (chunk, _) in Terrain.VisibleChunks)
            _owner.ChunkDraws.Add(chunk.OpaqueSlice!.Value, chunk.WorldMin,
                MathF.Min(1f, (float)(chunk.lifetime / Chunk.MAX_LIFETIME)));

        _owner.ChunkDraws.Draw(_owner.OpaqueChunkGeometry.Vao, _owner.ChunkShader, _owner.LocChunkDrawBase);
        // The LOD regions below ride the same program and the same ring: _owner.ChunkShader is
        // still in use from the chunk pass above and _owner.LocChunkViewProj already holds
        // _reflViewProj.

        Terrain.VisibleRegions.Clear();
        for (var i = 0; i < regions.Count; i++)
        {
            var region = regions[i];
            if (!region.HasMesh || region.IsBuried) continue;
            // The mirror clips below RenderManager.WaterPlaneY, so geometry wholly under it draws no pixel.
            if (!ReflectionCull.MayReachPlane(region.YMax)) continue;

            var min = new Vector3(region.X * region.SizeBlocks, region.YMin, region.Z * region.SizeBlocks);
            var max = new Vector3(min.X + region.SizeBlocks, region.YMax, min.Z + region.SizeBlocks);

            if (!frustum.IntersectsAABB(min, max)) continue;

            Terrain.VisibleRegions.Add((region, 0f));
        }

        // Deliberately unsorted: the list above adds (region, 0f) and the mirror has always
        // drawn regions in list order. Do not "fix" it into one of LodRegion's comparators —
        // that would change what the mirror shows, which is not this item's business.

        _owner.ChunkDraws.BeginPass(Terrain.VisibleRegions.Count);

        // The mirror does not advance Lifetime; it reads the value TerrainPasses.RenderLod left
        // last frame.
        foreach (var (region, _) in Terrain.VisibleRegions)
            _owner.ChunkDraws.Add(region.OpaqueSlice!.Value,
                new Vector3(region.X * region.SizeBlocks, 0f, region.Z * region.SizeBlocks),
                MathF.Min(1f, (float)(region.Lifetime / LodRegion.MAX_LIFETIME)));

        _owner.ChunkDraws.Draw(_owner.LodGeometry.Vao, _owner.ChunkShader, _owner.LocChunkDrawBase);

        // Entities, while blend is still off and Lighting.CameraPos/_owner.ClipY hold the mirrored
        // values (the clip drops anything below the plane). Invariant: unit 2 stays 0 for the
        // whole mirror pass, since _reflTex is this pass's target - so only the atlas is rebound
        // afterwards, never through RenderManager.BindWorldTextures.
        DrawMirrorEntities(camera, entities.Player);
        _owner.WorldShader.Use();
        Units.BindUnit(0, TextureTarget.Texture2DArray, _owner.AtlasTexture);
        Units.ActiveUnit(0);

        // Clouds in the mirror are what makes the reflection readable on open water (the dome
        // alone is only a smooth gradient). The mirror flips triangle winding, so cull front faces
        // here to keep the same "nearest exterior surface wins" behavior SkyPasses.RenderClouds
        // relies on.
        if (_owner.Clouds != null && savedCamPos.Y >= RenderManager.WaterPlaneY)
        {
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            _gl.DepthMask(false);
            _gl.Enable(EnableCap.CullFace);
            _gl.CullFace(TriangleFace.Front);
            // The clouds mix over the mirror in display space by reading it (CloudRenderer.Render).
            Units.BindUnit(RenderManager.SceneReadUnit, TextureTarget.Texture2D, _reflTex);
            _owner.Clouds.Render(_reflViewProj, mirroredPos, Lighting.TimeD, Lighting.SunDir, Lighting.SunSrgb, Lighting.CloudSunIntensity, Lighting.CloudAmbientSrgb,
                Lighting.FogColor, Lighting.FogNear, Lighting.FogFar, Lighting.Sky, Lighting.FogWet, RenderManager.SceneReadUnit);
            Units.BindUnit(RenderManager.SceneReadUnit, TextureTarget.Texture2D, 0);
            Units.ActiveUnit(0);
            _gl.CullFace(TriangleFace.Back);
            _gl.Disable(EnableCap.CullFace);
            _gl.DepthMask(true);
            // _owner.Clouds.Render bound its own program; ApplyFrameUniforms below needs ours.
            _owner.WorldShader.Use();
        }

        Lighting.CameraPos = savedCamPos;
        _owner.ClipY = savedClip;
        // Back to the HDR scene target the rest of the frame draws into
        Targets.BindSceneTarget();
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        // Restores the real eye's fog/camera uniforms; uReflViewProj survives in the program.
        _owner.ApplyFrameUniforms();
        _owner.SetWorldUniform("uReflValid", 1f);
        _owner.SetWorldUniform("uWaterSsrMode", 1f);
        // _reflTex now matches this camera, so the next frame may reuse it (once).
        _reflPose = pose;
        (_reflEntityPositions, _reflEntityScratch) = (_reflEntityScratch, _reflEntityPositions);
        _reflMirrorValid = true;
        _reflReusedLastFrame = false;
    }

    /// <summary>
    /// Fills the per-kind mirror draw lists and the position scratch the motion signal compares,
    /// in the fixed order drops, thrown, primed, falling, slimes, goblins, toppling. An entity counts only
    /// within <see cref="WaterRenderConfig.ReflectionEntityRadius"/> (horizontally) of the eye
    /// and no lower than a block under the water plane; the player is covered by the camera pose.
    /// </summary>
    private void GatherMirrorEntities(Vector3 eye, in ReflectionEntities entities)
    {
        _reflEntityScratch.Clear();
        _mirrorDrops.Clear();
        _mirrorThrown.Clear();
        _mirrorPrimed.Clear();
        _mirrorFalling.Clear();
        _mirrorSlimes.Clear();
        _mirrorGoblins.Clear();
        _mirrorIcebergs.Clear();
        _mirrorToppling.Clear();

        foreach (var drop in entities.Drops)
            if (KeepForMirror(eye, drop.Position)) _mirrorDrops.Add(drop);
        foreach (var projectile in entities.Thrown)
            if (KeepForMirror(eye, projectile.Position)) _mirrorThrown.Add(projectile);
        foreach (var tnt in entities.Primed)
            if (KeepForMirror(eye, tnt.Position)) _mirrorPrimed.Add(tnt);
        foreach (var block in entities.Falling)
            if (KeepForMirror(eye, block.Position)) _mirrorFalling.Add(block);
        if (Mobs.SlimeMesh != null)
            foreach (var slime in entities.Slimes)
                if (KeepForMirror(eye, slime.Position)) _mirrorSlimes.Add(slime);
        if (Mobs.GoblinRig != null)
            foreach (var goblin in entities.Goblins)
                if (KeepForMirror(eye, goblin.Position)) _mirrorGoblins.Add(goblin);
        // A berg's waterline center feeds the reuse motion signal, so a drifting berg refreshes it
        foreach (var berg in entities.Icebergs)
            if (KeepForMirror(eye, berg.Center)) _mirrorIcebergs.Add(berg);
        // A felled tree's base stays put while it swings, so its crown stands in for it.
        foreach (var tree in entities.Toppling)
            if (KeepForMirror(eye, Vector3.Transform(new Vector3(0f, 4f, 0f), tree.Model)))
                _mirrorToppling.Add(tree);
    }

    private bool KeepForMirror(Vector3 eye, Vector3 position)
    {
        var dx = position.X - eye.X;
        var dz = position.Z - eye.Z;
        var r = WaterRenderConfig.ReflectionEntityRadius;
        if (dx * dx + dz * dz > r * r || position.Y < RenderManager.WaterPlaneY - 1f) return false;
        _reflEntityScratch.Add(position);
        return true;
    }

    /// <summary>
    /// Draws the gathered entities and the whole player body into the mirror through
    /// <c>_reflViewProj</c>. Every core runs in mirror mode, so unit 2 is never bound to the
    /// reflection texture this pass renders into.
    /// </summary>
    private void DrawMirrorEntities(Camera camera, PlayerModel? player)
    {
        if (_mirrorDrops.Count > 0) Objects.DrawItemDrops(_mirrorDrops, _reflViewProj, mirror: true);
        if (_mirrorThrown.Count > 0) Objects.DrawThrown(_mirrorThrown, _reflViewProj, mirror: true);
        if (_mirrorPrimed.Count > 0) Objects.DrawPrimedTnt(_mirrorPrimed, _reflViewProj, mirror: true);
        if (_mirrorFalling.Count > 0) Objects.DrawFallingBlocks(_mirrorFalling, _reflViewProj, mirror: true);
        if (_mirrorSlimes.Count > 0) Mobs.DrawSlimes(_mirrorSlimes, _reflViewProj, mirror: true);
        if (_mirrorGoblins.Count > 0) Mobs.DrawGoblins(_mirrorGoblins, _reflViewProj, mirror: true);
        if (_mirrorIcebergs.Count > 0) _owner.IcebergPasses.DrawIcebergs(_reflViewProj, _mirrorIcebergs, mirror: true);
        foreach (var tree in _mirrorToppling) Objects.DrawTopplingTree(tree, _reflViewProj, mirror: true);
        if (player != null) Players.DrawPlayer(camera, player, _reflViewProj, fullBody: true, mirror: true);
    }

    /// <summary>Drops the mirror's entity lists, which still point into the torn-down world, and
    /// marks the mirror invalid so the next world renders a fresh one instead of reusing the old
    /// image. Called by <see cref="RenderManager.ForgetWorld"/>.</summary>
    internal void ForgetWorld()
    {
        _mirrorDrops.Clear();
        _mirrorThrown.Clear();
        _mirrorPrimed.Clear();
        _mirrorFalling.Clear();
        _mirrorSlimes.Clear();
        _mirrorGoblins.Clear();
        _mirrorIcebergs.Clear();
        _mirrorToppling.Clear();
        _reflMirrorValid = false;
    }
}
