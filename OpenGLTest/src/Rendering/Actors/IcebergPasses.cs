using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Data;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Icebergs;
using VoxelEngine.Graphics;

namespace VoxelEngine.Rendering.Actors;

/// <summary>
/// The iceberg draws: one baked mesh and skin per variant (size class), built from the iceberg
/// entity's Blockbench projects at load, drawn for the entity pass, the near shadow cascade and the
/// water mirror. Each berg is placed by <see cref="IcebergShape.ModelMatrix"/> (the very turn,
/// scale and placement its collision boxes use) with its render-only wobble and its rise or sink
/// on top (<see cref="ModelMatrix"/>). No iceberg entity means no meshes and no-op passes. Render
/// thread only.
/// </summary>
internal sealed class IcebergPasses : IDisposable
{
    private readonly GL _gl;
    private readonly RenderManager _owner;
    private readonly Mesh[] _meshes = [];
    private readonly uint[] _textures = [];

    private TextureUnits Units => _owner.Units;

    internal IcebergPasses(GL gl, RenderManager owner)
    {
        _gl = gl;
        _owner = owner;
        if (EntityRegistry.TryGet("iceberg") is not { } type)
            return;

        _meshes = new Mesh[type.Variants.Count];
        _textures = new uint[type.Variants.Count];
        for (var i = 0; i < _meshes.Length; i++)
        {
            // The parse happened once at content load (EntityType.Variants); only GL work is left
            var model = EntityModel.FromData(type.Variants[i], type.VariantPaths[i]);
            _meshes[i] = model.BuildMesh(gl);
            _textures[i] = TextureLoader.Upload(gl, model.TexturePixels, model.TextureWidth, model.TextureHeight,
                srgb: true);
        }
    }

    /// <summary>The variant's mesh, for the shadow pass; null when the variant is unknown.</summary>
    internal Mesh? MeshOf(int variant) => variant >= 0 && variant < _meshes.Length ? _meshes[variant] : null;

    /// <summary>The variant's skin texture, for the shadow pass's alpha test (only valid with a <see cref="MeshOf"/> mesh).</summary>
    internal uint TextureOf(int variant) => _textures[variant];

    /// <summary>
    /// The lighting clock the wobble is sampled at this frame, set once before the first pass that
    /// draws a berg (the shadows), so the shadow, the mirror and the entity pass agree.
    /// </summary>
    internal double Time { get; set; }

    /// <summary>
    /// A berg's model-to-world matrix at lighting time <paramref name="time"/>: its wobble's pitch and
    /// roll about the waterline center, then <see cref="IcebergShape.ModelMatrix"/> at the center
    /// raised by the bob and lowered by its rise or sink (<see cref="Iceberg.VisualSinkDepth"/>).
    /// </summary>
    internal static Matrix4x4 ModelMatrix(Iceberg berg, double time)
    {
        var (bob, pitch, roll) = IcebergWobble.Sample(berg.Seed, time, berg.Scale);
        var center = berg.Center + new Vector3(0f, bob - berg.VisualSinkDepth, 0f);
        return Matrix4x4.CreateRotationX(pitch) * Matrix4x4.CreateRotationZ(roll)
               * IcebergShape.ModelMatrix(berg.Turns, berg.Scale, center);
    }

    /// <summary>Draws the live bergs the camera can see into the entity pass.</summary>
    internal void RenderIcebergs(Matrix4x4 viewProj, IReadOnlyList<Iceberg> bergs)
    {
        if (_meshes.Length == 0 || bergs.Count == 0) return;
        DrawIcebergs(viewProj, bergs, mirror: false);
    }

    /// <summary>
    /// Draws <paramref name="bergs"/> through the world program: the entity pass or (with
    /// <paramref name="mirror"/>) the water mirror. A berg outside the view frustum is skipped; each
    /// is lit by the voxel light over its top. Restores the world textures on the way out.
    /// </summary>
    internal void DrawIcebergs(Matrix4x4 viewProj, IReadOnlyList<Iceberg> bergs, bool mirror)
    {
        if (_meshes.Length == 0 || bergs.Count == 0) return;

        var frustum = new Frustum(viewProj);
        _owner.WorldShader.Use();
        _owner.BindEntityTextures(mirror);
        foreach (var berg in bergs)
        {
            if (berg.Variant < 0 || berg.Variant >= _meshes.Length) continue;
            var (min, max) = berg.Bounds;
            if (!frustum.IntersectsAABB(min, max)) continue;

            Units.BindUnit(0, TextureTarget.Texture2DArray, _textures[berg.Variant]);
            var model = ModelMatrix(berg, Time);
            var top = new Vector3((min.X + max.X) * 0.5f, max.Y + 0.5f, (min.Z + max.Z) * 0.5f);
            _owner.SetDrawUniforms(model, model * viewProj, 1f, _owner.LightAt(top));
            _meshes[berg.Variant].Render();
        }
        _owner.BindEntityTextures(mirror);
    }

    /// <summary>Frees the variant meshes and skins.</summary>
    public void Dispose()
    {
        foreach (var mesh in _meshes)
            mesh.Dispose();
        foreach (var texture in _textures)
            if (texture != 0)
                _gl.DeleteTexture(texture);
    }
}
