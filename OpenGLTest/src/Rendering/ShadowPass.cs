using System.Collections.Generic;
using System.Numerics;
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
using VoxelEngine.Rendering.Geometry;
using VoxelEngine.Simulation;
using VoxelEngine.UI;

namespace VoxelEngine.Rendering;

/// <summary>
/// The depth-only sun shadow passes run first each frame: the near cascade with the chunk,
/// entity and player casters, the far cascade with the chunk casters and the non-buried LOD
/// regions, each chunk set through one indirect draw of the CHUNK_DRAW shadow program. The
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

    private FrameLighting Lighting => _owner.Lighting;
    private TextureUnits Units => _owner.Units;
    private RenderTargets Targets => _owner.Targets;
    private MobPasses Mobs => _owner.Mobs;
    private PlayerPasses Players => _owner.Players;

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
    /// before anything draws to the scene target; water never casts. Culling uses each
    /// cascade's light frustum, so the caster set is exactly what its box can hold.
    /// <paramref name="chunks"/> is <see cref="ChunkManager.Renderable"/>, a superset of the
    /// casters: water-only chunks are in it and are skipped here. The chunk casters that
    /// survive the culls are drawn with a single <c>glMultiDrawElementsIndirect</c> per cascade
    /// through <see cref="ChunkDrawBatch"/> (<see cref="DrawChunkCasters"/>); TNT, falling
    /// blocks and trees keep the plain program.
    /// </summary>
    internal void RenderShadows(Camera camera, IReadOnlyList<Chunk> chunks,
        IReadOnlyList<PrimedTnt> primed, IReadOnlyList<FallingBlock> falling, IEnumerable<TopplingTree> trees,
        PlayerModel? player = null, IReadOnlyList<Slime>? slimes = null,
        IReadOnlyList<LodRegion>? lodRegions = null, IReadOnlyList<Goblin>? goblins = null,
        IReadOnlyList<Iceberg>? icebergs = null)
    {
        // The dry strength, not the wet-faded one: submerged, the caustics and the sun shafts
        // still sample this map even though the world diffuse has stopped using it.
        if (camera == null || Lighting.ShadowStrengthDry <= 0f) return;

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
        DrawChunkCasters(chunks, frustum);
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

        if (goblins != null && goblins.Count > 0 && Mobs.GoblinMeshes != null && Mobs.GoblinRig != null
            && Mobs.GoblinPartNames != null)
        {
            // The goblin skin on unit 0, like the player casters: cutout texels would otherwise
            // be alpha-tested against the atlas.
            Units.BindUnit(0, TextureTarget.Texture2DArray, Mobs.GoblinTexture);
            foreach (var goblin in goblins)
            {
                var feet = MobPasses.GoblinFeet(goblin);
                for (var i = 0; i < Mobs.GoblinMeshes.Length; i++)
                {
                    var model = goblin.Animation.GetPartMatrix(Mobs.GoblinPartNames[i], Mobs.GoblinRig, feet, goblin.Facing);
                    _owner.ShadowShader.SetUniform("uModel", model);
                    _owner.ShadowShader.SetUniform("uLightMVP", model * _owner.NearShadow.ViewProj);
                    Mobs.GoblinMeshes[i].Render();
                }

                // The flint in the hand casts too, on its own texture.
                if (goblin.Ammo > 0 && _owner.ItemMeshes.TryGet(Mobs.FlintId, out var stone, out var stoneTexture))
                {
                    Units.BindUnit(0, TextureTarget.Texture2DArray, stoneTexture);
                    var held = Matrix4x4.CreateScale(Goblin.HeldStoneScale)
                               * goblin.Animation.HandAnchor(Goblin.HandPart, Mobs.GoblinRig, feet, goblin.Facing);
                    _owner.ShadowShader.SetUniform("uModel", held);
                    _owner.ShadowShader.SetUniform("uLightMVP", held * _owner.NearShadow.ViewProj);
                    stone.Render();
                    Units.BindUnit(0, TextureTarget.Texture2DArray, Mobs.GoblinTexture);
                }
            }
            Units.BindUnit(0, TextureTarget.Texture2DArray, _owner.AtlasTexture);
        }

        if (icebergs != null)
            DrawIcebergCasters(icebergs, frustum);

        foreach (var tree in trees)
        {
            tree.EnsureMesh(_gl);
            var mesh = tree.Mesh;
            if (mesh == null) continue;

            var model = tree.Model;
            _owner.ShadowShader.SetUniform("uModel", model);
            _owner.ShadowShader.SetUniform("uLightMVP", model * _owner.NearShadow.ViewProj);
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

        // The far cascade: the same depth-only chunk casters into its own coarse map. No entity
        // casters - they are sub-texel at this range, and skipping them keeps the pass to one
        // indirect draw.
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _owner.FarShadow.Fbo);
        _gl.Viewport(0, 0, (uint)_owner.FarShadow.MapSize, (uint)_owner.FarShadow.MapSize);
        _gl.Clear(ClearBufferMask.DepthBufferBit);
        // Same feedback-loop guard as unit 1 above: this pass renders into the far map.
        Units.BindUnit(RenderManager.FarShadowUnit, TextureTarget.Texture2D, 0);
        Units.BindUnit(0, TextureTarget.Texture2DArray, _owner.AtlasTexture);
        _owner.ChunkShadowShader.Use();
        _owner.ChunkShadowShader.SetUniform(_locChunkShadowViewProj, _owner.FarShadow.ViewProj);
        var farFrustum = new Frustum(_owner.FarShadow.ViewProj);
        DrawChunkCasters(chunks, farFrustum);

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

        // Back to the HDR scene target (which restores the viewport) and the blend state the
        // world passes expect
        Targets.BindSceneTarget();
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
    }

    /// <summary>
    /// Appends every opaque chunk caster that survives the mesh, burial and light-frustum culls
    /// to one <see cref="ChunkDrawBatch"/> pass and draws it with a single
    /// <c>glMultiDrawElementsIndirect</c>. <c>_owner.ChunkShadowShader</c> must be bound with the
    /// cascade's view-proj already set.
    /// </summary>
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

    private void DrawChunkCasters(IReadOnlyList<Chunk> chunks, Frustum frustum)
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

            _owner.ChunkDraws.Add(chunk.OpaqueSlice!.Value, chunk.WorldMin, 1f);
        }

        _owner.ChunkDraws.Draw(_owner.OpaqueChunkGeometry.Vao, _owner.ChunkShadowShader, _locChunkShadowDrawBase);
    }

    private void DrawPlayerPartShadow(PlayerModel player, string part, Camera camera)
    {
        var model = player.GetPartMatrix(part, camera.Position, camera.PlayerYaw, camera.Pitch);
        _owner.ShadowShader.SetUniform("uModel", model);
        _owner.ShadowShader.SetUniform("uLightMVP", model * _owner.NearShadow.ViewProj);
        Players.PlayerMeshes[part].Render();
    }
}
