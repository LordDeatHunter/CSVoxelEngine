using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Chunks;
using VoxelEngine.Data;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Goblins;
using VoxelEngine.Entities.Icebergs;
using VoxelEngine.Entities.Sandworms;
using VoxelEngine.Entities.Slimes;
using VoxelEngine.Lod;
using VoxelEngine.Player;
using VoxelEngine.Rendering.Actors;
using VoxelEngine.Rendering.Geometry;
using VoxelEngine.Simulation;
using VoxelEngine.UI;

namespace VoxelEngine.Rendering;

/// <summary>
/// The depth-only sun shadow passes run first each frame: the near cascade with the chunk,
/// entity and player casters, the far cascade with the chunk casters and the non-buried LOD
/// regions, each chunk set through one indirect draw of the CHUNK_DRAW shadow program. The far
/// map is cached and re-rendered only when <see cref="FarShadowReuse"/> asks. The
/// cascades, both shadow programs and the chunk-draw ring stay owned by
/// <see cref="RenderManager"/>, and <see cref="FrameLighting"/> places the cascades' light
/// boxes. Render thread only.
/// </summary>
internal sealed class ShadowPass
{
    private readonly GL _gl;
    private readonly RenderManager _owner;

    // The CHUNK_DRAW shadow program's uniform locations, resolved once by the constructor.
    private int _locChunkShadowViewProj;
    private int _locChunkShadowDrawBase;
    private int _locChunkShadowTime;
    private int _locChunkShadowSway;
    private int _locChunkShadowWind;
    private int _locChunkShadowWindOffset;
    private int _locChunkShadowOpaque;

    // The near cascade only: which chunk casters can reach a texel any consumer samples this frame.
    // The far map is cached (FarShadowReuse) and re-rendered from its whole box, never receiver-culled.
    private readonly ShadowCasterCull _nearCull = new();

    private FrameLighting Lighting => _owner.Lighting;
    private TextureUnits Units => _owner.Units;
    private RenderTargets Targets => _owner.Targets;
    private MobPasses Mobs => _owner.Mobs;
    private PlayerPasses Players => _owner.Players;
    private GpuPassTimer GpuTimer => _owner.GpuTimer;

    internal ShadowPass(GL gl, RenderManager owner)
    {
        _gl = gl;
        _owner = owner;
        _locChunkShadowViewProj = _owner.ChunkShadowShader.GetUniformLocation("uViewProj");
        _locChunkShadowDrawBase = _owner.ChunkShadowShader.GetUniformLocation("uDrawBase");
        _locChunkShadowTime = _owner.ChunkShadowShader.GetUniformLocation("uTime");
        _locChunkShadowSway = _owner.ChunkShadowShader.GetUniformLocation("uSwayStrength");
        _locChunkShadowWind = _owner.ChunkShadowShader.GetUniformLocation("uWind");
        _locChunkShadowWindOffset = _owner.ChunkShadowShader.GetUniformLocation("uWindOffset");
        _locChunkShadowOpaque = _owner.ChunkShadowShader.GetUniformLocation("uOpaqueCasters");
    }

    /// <summary>
    /// Depth-only passes that fill both shadow cascades. The near pass renders every caster
    /// inside the near light box: opaque chunk meshes, primed TNT, falling blocks, slimes,
    /// goblins (<paramref name="goblins"/>), icebergs (<paramref name="icebergs"/>, each on its own skin), toppling trees and the player. The far pass then renders only the opaque chunk casters
    /// inside the far box - entities are sub-texel at that range - plus every non-buried LOD
    /// region (<paramref name="lodRegions"/>) it holds, drawn with opaque foliage so the LOD
    /// ring is shadowed by its own hills and canopy out to the far box. Both run first each frame,
    /// before anything draws to the scene target; water never casts. The far map is cached: it is
    /// re-rendered only on the frames <see cref="FarShadowReuse"/> asks for
    /// (<see cref="ShadowCascade.RenderThisFrame"/>, decided by <see cref="FrameLighting"/>), and
    /// each re-render is confirmed with <see cref="ShadowCascade.MarkRendered"/>. A re-render draws
    /// the full set of chunk and LOD casters in the far box, with no receiver cull, so the cached
    /// map stays valid for any later view. Only the near cascade's chunk casters are culled to
    /// those whose light-space footprint can reach a texel any consumer samples this frame
    /// (<see cref="ShadowCasterCull"/>: the camera pyramid, plus the mirror's whenever it may
    /// render, unbounded along the sun), which leaves every sampled texel bit-identical; entity
    /// casters stay culled to the near box only.
    /// <paramref name="chunks"/> is <see cref="ChunkManager.Renderable"/>, a superset of the
    /// casters: water-only chunks are in it and are skipped here. The chunk casters that
    /// survive the culls are drawn with a single <c>glMultiDrawElementsIndirect</c> per cascade
    /// through <see cref="ChunkDrawBatch"/> (<see cref="DrawChunkCasters"/>); TNT, falling
    /// blocks and trees keep the plain program. It times itself as the <c>shadowNear</c> and
    /// <c>shadowFar</c> profiler rows (CPU and GPU), split where the far cascade starts, because
    /// TIME_ELAPSED ranges cannot nest; both brackets wrap the whole body after the one early
    /// return, so no range is ever left open, and the <c>shadowFar</c> row is near zero on a
    /// frame that reuses the cached map.
    /// </summary>
    internal void RenderShadows(Camera camera, IReadOnlyList<Chunk> chunks,
        IReadOnlyList<PrimedTnt> primed, IReadOnlyList<FallingBlock> falling, IEnumerable<TopplingTree> trees,
        PlayerModel? player = null, IReadOnlyList<Slime>? slimes = null,
        IReadOnlyList<LodRegion>? lodRegions = null, IReadOnlyList<Goblin>? goblins = null,
        IReadOnlyList<Iceberg>? icebergs = null, IReadOnlyList<RiggedMob>? hellMobs = null,
        IReadOnlyList<RiggedMob>? skyMobs = null, IReadOnlyList<Sandworm>? sandworms = null,
        IReadOnlyList<RiggedMob>? skeletons = null, IReadOnlyList<RiggedMob>? skeletonKings = null,
        IReadOnlyList<RiggedMob>? caveMobs = null)
    {
        // The dry strength, not the wet-faded one: submerged, the caustics and the sun shafts
        // still sample this map even though the world diffuse has stopped using it.
        if (camera == null || Lighting.ShadowStrengthDry <= 0f) return;

        FrameProfiler.Begin(ProfileSection.ShadowNear);
        GpuTimer.BeginGpuPass(ProfileSection.ShadowNear);

        // The near kernel's diagonal taps mirror the shader's uShadowSoftness <= 0 branch; the far
        // cascade is read with one tap.
        BeginReceivers(_nearCull, _owner.NearShadow, pcfKernel: LightingConfig.ShadowSoftnessTexels > 0f, camera);

        var size = (uint)_owner.NearShadow.MapSize;

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _owner.NearShadow.Fbo);
        _gl.Viewport(0, 0, size, size);
        _gl.Clear(ClearBufferMask.DepthBufferBit);
        // No color is written, so blending would only cost fill rate
        _gl.Disable(EnableCap.Blend);

        _owner.ShadowShader.Use();
        // The depth texture is this pass's render target, so it must not stay bound to unit 1
        // while it draws: sampling and rendering the same texture is a feedback loop, and
        // drivers are free to return garbage for it. RenderManager.BindWorldTextures rebinds it
        // afterwards.
        Units.BindUnit(1, TextureTarget.Texture2D, 0);
        Units.BindUnit(0, TextureTarget.Texture2DArray, _owner.AtlasTexture);
        _owner.ShadowShader.SetUniform("uTime", Lighting.Time);
        _owner.ShadowShader.SetUniform("uSwayStrength", GameSettings.LeafSway);
        _owner.ShadowShader.SetUniform("uWind", Lighting.WindDirection * SkyConfig.WindLean);
        _owner.ShadowShader.SetUniform("uWindOffset", Lighting.WindOffset);

        var frustum = new Frustum(_owner.NearShadow.ViewProj);

        // The chunk casters go through the CHUNK_DRAW variant and one MultiDrawElementsIndirect;
        // the entity loops below keep the plain program, which is still bound above for them.
        _owner.ChunkShadowShader.Use();
        _owner.ChunkShadowShader.SetUniform(_locChunkShadowTime, Lighting.Time);
        _owner.ChunkShadowShader.SetUniform(_locChunkShadowSway, GameSettings.LeafSway);
        _owner.ChunkShadowShader.SetUniform(_locChunkShadowWind, Lighting.WindDirection * SkyConfig.WindLean);
        _owner.ChunkShadowShader.SetUniform(_locChunkShadowWindOffset, Lighting.WindOffset);
        _owner.ChunkShadowShader.SetUniform(_locChunkShadowViewProj, _owner.NearShadow.ViewProj);
        DrawChunkCasters(chunks, frustum, _nearCull);
        _owner.ShadowShader.Use();

        foreach (var tnt in primed)
        {
            var mesh = _owner.BlockCubes.Get(tnt.BlockId);
            if (mesh == null) continue;

            var model = Matrix4x4.CreateScale(tnt.Scale)
                        * Matrix4x4.CreateTranslation(tnt.Position);
            _owner.ShadowShader.SetUniform("uModel", model);
            _owner.ShadowShader.SetUniform("uLightMVP", model * _owner.NearShadow.ViewProj);
            mesh.Render();
        }

        foreach (var block in falling)
        {
            var mesh = _owner.BlockCubes.Get(block.BlockId);
            if (mesh == null) continue;

            var model = Matrix4x4.CreateTranslation(block.Position);
            _owner.ShadowShader.SetUniform("uModel", model);
            _owner.ShadowShader.SetUniform("uLightMVP", model * _owner.NearShadow.ViewProj);
            mesh.Render();
        }

        if (slimes != null && Mobs.SlimeMesh != null)
        {
            foreach (var slime in slimes)
            {
                var model = MobPasses.SlimeTransform(slime);
                _owner.ShadowShader.SetUniform("uModel", model);
                _owner.ShadowShader.SetUniform("uLightMVP", model * _owner.NearShadow.ViewProj);
                Mobs.SlimeMesh.Render();
            }
        }

        if (goblins != null && goblins.Count > 0 && Mobs.GoblinRig is { } goblinRig)
        {
            // The goblin skin on unit 0, like the player casters: cutout texels would otherwise
            // be alpha-tested against the atlas.
            Units.BindUnit(0, TextureTarget.Texture2DArray, goblinRig.Texture);
            foreach (var goblin in goblins)
            {
                DrawRigCaster(goblin, goblinRig);

                // The flint in the hand casts too, on its own texture.
                if (goblin.Ammo > 0 && _owner.ItemMeshes.TryGet(Mobs.FlintId, out var stone, out var stoneTexture))
                {
                    Units.BindUnit(0, TextureTarget.Texture2DArray, stoneTexture);
                    var held = Matrix4x4.CreateScale(Goblin.HeldStoneScale)
                               * goblin.Animation.HandAnchor(Goblin.HandPart, goblinRig.Rig, goblin.Feet, goblin.Facing);
                    _owner.ShadowShader.SetUniform("uModel", held);
                    _owner.ShadowShader.SetUniform("uLightMVP", held * _owner.NearShadow.ViewProj);
                    stone.Render();
                    Units.BindUnit(0, TextureTarget.Texture2DArray, goblinRig.Texture);
                }
            }
            Units.BindUnit(0, TextureTarget.Texture2DArray, _owner.AtlasTexture);
        }

        DrawRealmCasters(hellMobs);
        DrawRealmCasters(skyMobs);
        DrawRealmCasters(skeletons);
        DrawRealmCasters(skeletonKings);
        DrawRealmCasters(caveMobs);
        DrawSandwormCasters(sandworms);

        if (icebergs != null)
            DrawIcebergCasters(icebergs, frustum);

        foreach (var tree in trees)
        {
            tree.EnsureMesh(_gl);
            if (tree.Meshes.Count == 0) continue;

            var model = tree.Model;
            _owner.ShadowShader.SetUniform("uModel", model);
            _owner.ShadowShader.SetUniform("uLightMVP", model * _owner.NearShadow.ViewProj);
            foreach (var mesh in tree.Meshes)
                mesh.Render();
        }

        if (player != null && camera != null)
        {
            Units.BindUnit(0, TextureTarget.Texture2DArray, Players.PlayerPaletteTex);
            foreach (var part in PlayerModel.BodyParts)
                DrawPlayerPartShadow(player, part, camera);
            DrawPlayerPartShadow(player, "Head", camera);
            Units.BindUnit(0, TextureTarget.Texture2DArray, _owner.AtlasTexture);
        }

        // The far cascade: the same depth-only chunk casters into its own coarse map, re-rendered
        // only when FarShadowReuse asks (a reuse frame keeps the cached map and its held view-proj).
        // The near timing range ends here and the far one starts: TIME_ELAPSED queries cannot
        // nest, so the two rows are consecutive, and both stay balanced on a reuse frame.
        GpuTimer.EndGpuPass(ProfileSection.ShadowNear);
        FrameProfiler.End(ProfileSection.ShadowNear);
        FrameProfiler.Begin(ProfileSection.ShadowFar);
        GpuTimer.BeginGpuPass(ProfileSection.ShadowFar);

        if (_owner.FarShadow.RenderThisFrame)
        {
            RenderFarCascade(chunks, lodRegions);
            _owner.FarShadow.MarkRendered();
        }

        // Back to the HDR scene target (which restores the viewport) and the blend state the
        // world passes expect
        Targets.BindSceneTarget();
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        GpuTimer.EndGpuPass(ProfileSection.ShadowFar);
        FrameProfiler.End(ProfileSection.ShadowFar);
    }

    /// <summary>
    /// Re-renders the cached far map through its held view-proj: every opaque chunk caster and
    /// every non-buried LOD region inside the far box, with no receiver cull, so the map stays
    /// valid for any view until <see cref="FarShadowReuse"/> next asks for a render. No entity
    /// casters - they are sub-texel at this range, and skipping them keeps the pass to two
    /// indirect draws. The caller restores the scene target and the blend state.
    /// </summary>
    private void RenderFarCascade(IReadOnlyList<Chunk> chunks, IReadOnlyList<LodRegion>? lodRegions)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _owner.FarShadow.Fbo);
        _gl.Viewport(0, 0, (uint)_owner.FarShadow.MapSize, (uint)_owner.FarShadow.MapSize);
        _gl.Clear(ClearBufferMask.DepthBufferBit);
        // Same feedback-loop guard as unit 1 above: this pass renders into the far map.
        Units.BindUnit(RenderManager.FarShadowUnit, TextureTarget.Texture2D, 0);
        Units.BindUnit(0, TextureTarget.Texture2DArray, _owner.AtlasTexture);
        _owner.ChunkShadowShader.Use();
        _owner.ChunkShadowShader.SetUniform(_locChunkShadowViewProj, _owner.FarShadow.ViewProj);
        var farFrustum = new Frustum(_owner.FarShadow.ViewProj);
        DrawChunkCasters(chunks, farFrustum, null);

        // The LOD ring casts into the far cascade only: the near box never reaches it. Buried
        // regions are skipped because loaded chunks or finer regions already cast over their
        // area. Lifetime and the fade-in alpha are TerrainPasses.RenderLod's alone and are not touched here;
        // a caster is always drawn at full alpha. A partly covered, non-buried level-0 region
        // also casts over the loaded chunks it overlaps - its shell sits under the real
        // surface, but its opaque canopy pillars could double up tree shadows at the 256-block
        // seam; if that ever shows, skip those regions here.
        if (lodRegions != null)
        {
            _owner.ChunkShadowShader.SetUniform(_locChunkShadowOpaque, 1);
            _owner.ChunkDraws.BeginPass(lodRegions.Count);

            for (var i = 0; i < lodRegions.Count; i++)
            {
                var region = lodRegions[i];
                if (!region.HasMesh || region.IsBuried) continue;

                var min = new Vector3(region.X * region.SizeBlocks, region.YMin, region.Z * region.SizeBlocks);
                var max = new Vector3(min.X + region.SizeBlocks, region.YMax, min.Z + region.SizeBlocks);
                if (!farFrustum.IntersectsAABB(min, max)) continue;

                _owner.ChunkDraws.Add(region.OpaqueSlice!.Value,
                    new Vector3(region.X * region.SizeBlocks, 0f, region.Z * region.SizeBlocks), 1f);
            }

            _owner.ChunkDraws.Draw(_owner.LodGeometry.Vao, _owner.ChunkShadowShader, _locChunkShadowDrawBase);
            _owner.ChunkShadowShader.SetUniform(_locChunkShadowOpaque, 0);
        }
    }

    // One realm's mobs (hell's or the sky's) into the near cascade, each on its own kind's skin; the atlas
    // goes back on unit 0 after them
    private void DrawRealmCasters(IReadOnlyList<RiggedMob>? mobs)
    {
        if (mobs == null || mobs.Count == 0) return;
        foreach (var mob in mobs)
        {
            if (Mobs.RealmRigOf(mob.Type) is not { } rig) continue;
            Units.BindUnit(0, TextureTarget.Texture2DArray, rig.Texture);
            DrawRigCaster(mob, rig);
        }
        Units.BindUnit(0, TextureTarget.Texture2DArray, _owner.AtlasTexture);
    }

    // The sandworm segments into the near cascade, on the worm own skin; the atlas goes back on unit 0 after them
    private void DrawSandwormCasters(IReadOnlyList<Sandworm>? worms)
    {
        if (worms == null || worms.Count == 0 || Mobs.SandwormRig is not { } rig) return;
        Units.BindUnit(0, TextureTarget.Texture2DArray, rig.Texture);
        foreach (var worm in worms)
        {
            foreach (var (model, mesh, _) in Mobs.GatherSandwormParts(worm))
            {
                _owner.ShadowShader.SetUniform("uModel", model);
                _owner.ShadowShader.SetUniform("uLightMVP", model * _owner.NearShadow.ViewProj);
                rig.Meshes[mesh].Render();
            }
        }
        Units.BindUnit(0, TextureTarget.Texture2DArray, _owner.AtlasTexture);
    }

    // Every part of one rigged mob into the near cascade, then the item its kind holds on its own sprite texture
    // (the cutout is alpha-tested); its skin is already bound on unit 0 and is bound again after the item
    private void DrawRigCaster(RiggedMob mob, RigMeshes rig)
    {
        for (var i = 0; i < rig.Meshes.Length; i++)
        {
            var model = rig.PartMatrix(mob, i);
            _owner.ShadowShader.SetUniform("uModel", model);
            _owner.ShadowShader.SetUniform("uLightMVP", model * _owner.NearShadow.ViewProj);
            rig.Meshes[i].Render();
        }

        var type = mob.Type;
        if (type.HeldItemId == 0 || !_owner.ItemMeshes.TryGet(type.HeldItemId, out var held, out var texture)) return;
        Units.BindUnit(0, TextureTarget.Texture2DArray, texture);
        var heldModel = type.HeldMatrix(mob.Animation, rig.Rig, mob.Feet, mob.Facing);
        _owner.ShadowShader.SetUniform("uModel", heldModel);
        _owner.ShadowShader.SetUniform("uLightMVP", heldModel * _owner.NearShadow.ViewProj);
        held.Render();
        Units.BindUnit(0, TextureTarget.Texture2DArray, rig.Texture);
    }

    // The bergs inside the near light box, each on its own skin like the goblin casters (its texels
    // would otherwise be alpha-tested against the atlas)
    private void DrawIcebergCasters(IReadOnlyList<Iceberg> icebergs, Frustum frustum)
    {
        var drew = false;
        foreach (var berg in icebergs)
        {
            var mesh = _owner.IcebergPasses.MeshOf(berg.Variant);
            if (mesh == null || !frustum.IntersectsAABB(berg.Bounds.Min, berg.Bounds.Max)) continue;
            Units.BindUnit(0, TextureTarget.Texture2DArray, _owner.IcebergPasses.TextureOf(berg.Variant));
            var model = IcebergPasses.ModelMatrix(berg, _owner.IcebergPasses.Time);
            _owner.ShadowShader.SetUniform("uModel", model);
            _owner.ShadowShader.SetUniform("uLightMVP", model * _owner.NearShadow.ViewProj);
            mesh.Render();
            drew = true;
        }
        if (drew)
            Units.BindUnit(0, TextureTarget.Texture2DArray, _owner.AtlasTexture);
    }

    /// <summary>
    /// Appends every opaque chunk caster that survives the mesh, burial, light-frustum and
    /// receiver (<paramref name="cull"/>) culls to one <see cref="ChunkDrawBatch"/> pass and draws
    /// it with a single <c>glMultiDrawElementsIndirect</c>. A null <paramref name="cull"/> (the
    /// cached far map) keeps every caster in the frustum. <c>_owner.ChunkShadowShader</c> must
    /// be bound with the cascade's view-proj already set.
    /// </summary>
    private void DrawChunkCasters(IReadOnlyList<Chunk> chunks, Frustum frustum, ShadowCasterCull? cull)
    {
        _owner.ChunkDraws.BeginPass(chunks.Count);

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];

            // HasMesh is the opaque mesh only, so a water-only chunk (translucent mesh and
            // nothing else) never enters the depth pass — fluids are not casters anyway.
            if (!chunk.HasMesh) continue;

            // Cull buried casters. A cascade's ortho box is twice its radius wide and four times
            // its radius deep, so its frustum admits every chunk of a column right down to
            // bedrock, and a chunk whose top sits below the lowest surface height in its own 16x16 column is
            // completely under the terrain: no sunlight reaches it, so it can occlude nothing.
            // A 1-block margin keeps the surface block itself (and the face under it) in.
            // Trees do poke above the surface, but only into the chunk ABOVE, whose own top is
            // then above that column's surface height and so is never skipped by this test.
            // (Written as top + 1 < min rather than top < min - 1 so the int.MinValue
            // sentinel on a chunk with no block data cannot wrap and cull everything.)
            if (chunk.UpperBoundY + 1 < chunk.SurfaceMinY) continue;

            if (!frustum.IntersectsAABB(chunk.WorldMin, chunk.WorldMax)) continue;

            // A caster whose footprint reaches no texel any consumer samples cannot change the frame.
            if (cull != null && !cull.MayShadowReceivers(chunk.WorldMin, chunk.WorldMax)) continue;

            _owner.ChunkDraws.Add(chunk.OpaqueSlice!.Value, chunk.WorldMin, 1f);
        }

        _owner.ChunkDraws.Draw(_owner.OpaqueChunkGeometry.Vao, _owner.ChunkShadowShader, _locChunkShadowDrawBase);
    }

    /// <summary>
    /// Starts <paramref name="cull"/> for <paramref name="cascade"/> this frame and adds every
    /// view that can sample it: the camera pyramid (the world program, the leaf backlight, the
    /// caustics and the rays/godrays marches) and the water mirror's whenever it may render. It
    /// serves the near cascade only: the far map is cached and re-rendered from its whole box, since
    /// a map culled to this frame's receivers would be stale for the next view.
    /// </summary>
    private static void BeginReceivers(ShadowCasterCull cull, ShadowCascade cascade, bool pcfKernel, Camera camera)
    {
        cull.Begin(cascade.ViewProj, cascade.MapSize, ShadowCasterCull.ReceiverReachTexels(pcfKernel),
            ShadowCasterCull.CasterReachBlocks);
        var projection = camera.GetProjectionMatrix();
        // Twice the far plane: headroom for the rays' and godrays' unprojection, which slides a
        // sky target along its view ray.
        var far = 2f * Camera.FarPlane;
        // RenderEye is Lighting.CameraPos, the marches' origin.
        cull.AddViewPyramid(camera.GetViewMatrix(), projection, far, camera.RenderEye);
        if (!ReflectionCull.MirrorMayRender(WorldGenerationConfig.WaterEnabled && GameSettings.WaterReflections,
                camera.RenderEye.Y, camera.IsEyeInWater))
            return;
        var mirrorView = ReflectionCull.MirrorView(camera.RenderEye, camera.ViewDirection,
            RenderManager.WaterPlaneY, out var mirroredEye, out _);
        cull.AddViewPyramid(mirrorView, projection, far, mirroredEye);
    }

    private void DrawPlayerPartShadow(PlayerModel player, string part, Camera camera)
    {
        var model = player.GetPartMatrix(part, camera.Position, camera.PlayerYaw, camera.Pitch);
        _owner.ShadowShader.SetUniform("uModel", model);
        _owner.ShadowShader.SetUniform("uLightMVP", model * _owner.NearShadow.ViewProj);
        Players.PlayerMeshes[part].Render();
    }
}
