using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Entities;
using VoxelEngine.Graphics;
using VoxelEngine.Items;
using VoxelEngine.Particles;
using VoxelEngine.Particles.Collapse;
using VoxelEngine.Player;
using VoxelEngine.Simulation;

namespace VoxelEngine.Rendering.Actors;

/// <summary>
/// The loose-object draws of the entity pass: the floating item drops, the thrown items in
/// flight, primed TNT, falling sand and gravel, the mining debris, the blocks a collapsing tower
/// throws off and the toppling trees. Each
/// Draw* core takes its own view-projection and a mirror flag, so the water mirror redraws the
/// same objects through them. Owns no GL resource: the world program and the item and block
/// caches stay owned by <see cref="RenderManager"/>. Render thread only.
/// </summary>
internal sealed class ObjectPasses
{
    private readonly GL _gl;
    private readonly RenderManager _owner;

    private TextureUnits Units => _owner.Units;

    internal ObjectPasses(GL gl, RenderManager owner)
    {
        _gl = gl;
        _owner = owner;
    }

    /// <summary>
    /// Draws the floating item drops, each spinning about its own center and pulsing in scale.
    /// Block drops draw their atlas cube; pure items (see <see cref="Item"/>) draw their
    /// extruded sprite with its own texture bound (the shader's sampler just reads unit 0).
    /// </summary>
    internal void RenderItemDrops(Camera camera, IReadOnlyList<ItemDrop> drops)
    {
        if (camera == null || drops.Count == 0) return;
        DrawItemDrops(drops, camera.GetViewProjection(), mirror: false);
    }

    internal void DrawItemDrops(IReadOnlyList<ItemDrop> drops, in Matrix4x4 viewProj, bool mirror)
    {
        _owner.WorldShader.Use();
        _owner.BindEntityTextures(mirror);

        // Only an item sprite ever takes unit 0 away from the atlas, so the atlas is rebound
        // when one actually did rather than once per drop. Unit 1 (the shadow map) never
        // changes in here.
        var atlasBound = true;

        foreach (var drop in drops)
        {
            Mesh? mesh;
            if (ItemRegistry.IsItem(drop.BlockId))
            {
                if (!_owner.ItemMeshes.TryGet(drop.BlockId, out mesh, out var itemTexture)) continue;
                Units.BindUnit(0, TextureTarget.Texture2DArray, itemTexture);
                atlasBound = false;
            }
            else
            {
                if (!atlasBound)
                {
                    _owner.BindEntityTextures(mirror);
                    atlasBound = true;
                }

                mesh = _owner.BlockCubes.Get(drop.BlockId);
                if (mesh == null) continue;
            }

            // The shape is centered on its own origin, so it spins in place
            var model = Matrix4x4.CreateScale(drop.Scale)
                        * Matrix4x4.CreateRotationY(drop.Spin)
                        * Matrix4x4.CreateTranslation(drop.Position);

            _owner.SetDrawUniforms(model, model * viewProj, 1f, _owner.LightAt(drop.Position));
            mesh.Render();
        }
    }

    internal void RenderThrown(Camera camera, IReadOnlyList<ThrownProjectile> thrown)
    {
        if (camera == null || thrown.Count == 0) return;
        DrawThrown(thrown, camera.GetViewProjection(), mirror: false);
    }

    internal void DrawThrown(IReadOnlyList<ThrownProjectile> thrown, in Matrix4x4 viewProj, bool mirror)
    {
        _owner.WorldShader.Use();
        _owner.BindEntityTextures(mirror);

        var atlasBound = true;

        foreach (var projectile in thrown)
        {
            Mesh? mesh;
            if (ItemRegistry.IsItem(projectile.ItemId))
            {
                if (!_owner.ItemMeshes.TryGet(projectile.ItemId, out mesh, out var itemTexture)) continue;
                Units.BindUnit(0, TextureTarget.Texture2DArray, itemTexture);
                atlasBound = false;
            }
            else
            {
                if (!atlasBound)
                {
                    _owner.BindEntityTextures(mirror);
                    atlasBound = true;
                }

                mesh = _owner.BlockCubes.Get(projectile.ItemId);
                if (mesh == null) continue;
            }

            var orientation = projectile.Aligned
                ? ThrownProjectile.AlignedRotation(projectile.Velocity)
                : Matrix4x4.CreateRotationY(projectile.Spin);
            var model = Matrix4x4.CreateScale(projectile.Scale)
                        * orientation
                        * Matrix4x4.CreateTranslation(projectile.Position);

            _owner.SetDrawUniforms(model, model * viewProj, 1f, _owner.LightAt(projectile.Position));
            mesh.Render();
        }
    }

    /// <summary>
    /// Draws the primed TNT cubes at full block size, strobing white through uFlash as their
    /// fuses burn down. Leaves uFlash at 0 so the other world draws stay textured.
    /// </summary>
    internal void RenderPrimedTnt(Camera camera, IReadOnlyList<PrimedTnt> primed)
    {
        if (camera == null || primed.Count == 0) return;
        DrawPrimedTnt(primed, camera.GetViewProjection(), mirror: false);
    }

    internal void DrawPrimedTnt(IReadOnlyList<PrimedTnt> primed, in Matrix4x4 viewProj, bool mirror)
    {
        _owner.WorldShader.Use();
        _owner.BindEntityTextures(mirror);

        var drewFlash = false;

        foreach (var tnt in primed)
        {
            var mesh = _owner.BlockCubes.Get(tnt.BlockId);
            if (mesh == null) continue;

            // The cube is centered on its own origin, so it scales about its center
            var model = Matrix4x4.CreateScale(tnt.Scale)
                        * Matrix4x4.CreateTranslation(tnt.Position);

            _owner.SetDrawUniforms(model, model * viewProj, 1f, _owner.LightAt(tnt.Position));
            _owner.WorldShader.SetUniform(_owner.LocFlash, tnt.WhiteFlash);
            mesh.Render();
            drewFlash = true;
        }

        // uFlash is a frame constant for every other pass, and the translucent water pass runs
        // after the entities, so put it back rather than tinting whatever draws next.
        if (drewFlash)
            _owner.WorldShader.SetUniform(_owner.LocFlash, 0f);
    }

    /// <summary>
    /// Draws falling sand/gravel cubes at full block size. Unlike primed TNT there is no
    /// flash or swell, so every cube shares one textured draw.
    /// </summary>
    internal void RenderFallingBlocks(Camera camera, IReadOnlyList<FallingBlock> falling)
    {
        if (camera == null || falling.Count == 0) return;
        DrawFallingBlocks(falling, camera.GetViewProjection(), mirror: false);
    }

    internal void DrawFallingBlocks(IReadOnlyList<FallingBlock> falling, in Matrix4x4 viewProj, bool mirror)
    {
        _owner.WorldShader.Use();
        _owner.BindEntityTextures(mirror);

        foreach (var block in falling)
        {
            var mesh = _owner.BlockCubes.Get(block.BlockId);
            if (mesh == null) continue;

            // The cube is centered on its own origin, like the primed TNT above
            var model = Matrix4x4.CreateTranslation(block.Position);

            _owner.SetDrawUniforms(model, model * viewProj, 1f, _owner.LightAt(block.Position));
            mesh.Render();
        }
    }

    /// <summary>
    /// Draws the live mining debris as small cubes textured as the block they broke off
    /// from (dust chips as its surface, <see cref="DebrisLook"/>). Each cube spins and tumbles about its center and fades through the world
    /// shader's alpha, like the item drops but smaller and never collected.
    /// </summary>
    internal void RenderBlockDebris(Camera camera, IReadOnlyList<BlockDebris> debris)
    {
        if (camera == null || debris.Count == 0) return;

        _owner.WorldShader.Use();
        _owner.BindWorldTextures();

        var viewProj = camera.GetViewProjection();

        foreach (var cube in debris)
        {
            var mesh = cube.Look switch
            {
                DebrisLook.Surface => _owner.BlockCubes.GetSurface(cube.BlockId),
                DebrisLook.Color => _owner.BlockCubes.GetColorChip(),
                _ => _owner.BlockCubes.GetTinted(cube.BlockId),
            };
            if (mesh == null) continue;

            var model = Matrix4x4.CreateScale(cube.Size)
                        * Matrix4x4.CreateRotationX(cube.Tumble)
                        * Matrix4x4.CreateRotationY(cube.Spin)
                        * Matrix4x4.CreateTranslation(cube.Position);

            _owner.SetDrawUniforms(model, model * viewProj, MathUtils.Clamp01(cube.Alpha), _owner.LightAt(cube.Position));
            // One cached cube per block id serves every cell, so a tinted chip's color rides
            // the per-draw uniform (white for an untinted block, whose mesh sets no bit 4)
            _owner.WorldShader.SetUniform(_owner.LocBiomeTint, cube.Tint);
            mesh.Render();
        }

        // Shader.SetUniform writes to the bound program, so this reset is only valid here,
        // with worldShader still bound; it keeps uBiomeTint white unless a tinted draw is in
        // flight, for the plain draws that follow (toppling tree, drops, entities)
        _owner.WorldShader.SetUniform(_owner.LocBiomeTint, Vector3.One);
    }

    /// <summary>
    /// Draws the blocks a collapsing tower throws off (<see cref="CollapseDebris"/>): whole cubes of the block that fell,
    /// tumbling and rolling about their centers, at full opacity until they burst into chips. Untinted, like the
    /// tower's own blocks, so <c>uBiomeTint</c> stays the white <see cref="RenderBlockDebris"/> leaves it at.
    /// </summary>
    internal void RenderCollapseDebris(Camera camera, IReadOnlyList<CollapseDebris> debris)
    {
        if (camera == null || debris.Count == 0) return;

        _owner.WorldShader.Use();
        _owner.BindWorldTextures();

        var viewProj = camera.GetViewProjection();
        foreach (var block in debris)
        {
            var mesh = _owner.BlockCubes.GetTinted(block.BlockId);
            if (mesh == null) continue;

            var model = Matrix4x4.CreateScale(block.Size)
                        * Matrix4x4.CreateFromQuaternion(block.Orientation)
                        * Matrix4x4.CreateTranslation(block.Position);
            _owner.SetDrawUniforms(model, model * viewProj, 1f, _owner.LightAt(block.Position));
            mesh.Render();
        }
    }

    /// <summary>
    /// Draws a felled tree swinging around its base. The mesh uploads on first sight (GL is
    /// only legal on this thread); an empty mesh means nothing visible was collected.
    /// </summary>
    internal void RenderTopplingTree(Camera camera, TopplingTree tree)
    {
        if (camera == null || tree == null) return;
        DrawTopplingTree(tree, camera.GetViewProjection(), mirror: false);
    }

    internal void DrawTopplingTree(TopplingTree tree, in Matrix4x4 viewProj, bool mirror)
    {
        tree.EnsureMesh(_gl);
        if (tree.Meshes.Count == 0) return;

        _owner.WorldShader.Use();
        _owner.BindEntityTextures(mirror);

        var model = tree.Model;
        _owner.SetDrawUniforms(model, model * viewProj, 1f, _owner.LightAt(model.Translation));

        foreach (var mesh in tree.Meshes)
            mesh.Render();
    }
}
