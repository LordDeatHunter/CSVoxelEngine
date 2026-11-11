using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.Content.Models;
using VoxelEngine.Data;
using VoxelEngine.Graphics;
using VoxelEngine.Player;
using VoxelEngine.Simulation.Chests;

namespace VoxelEngine.Rendering.Actors;

/// <summary>
/// The chest draws. A chest has no geometry in its chunk's mesh: every chest cell a mesh recorded
/// (<see cref="Chunk.ChestCells"/>) is drawn here from the one shared chest model (<see cref="ChestModel"/>), its
/// base and its lid posed by what that chest's lid is doing (<see cref="ChestLids"/>) and wearing the skin its
/// block names (<c>chest.skin</c>), for the entity pass and the near shadow cascade. Owns the two part meshes and
/// one texture per skin, built at load; content without a chest model means no meshes and no-op passes. Render
/// thread only.
/// </summary>
internal sealed class ChestPasses : IDisposable
{
    // How far past its chunk a chest can reach: an open lid swings out of its cell by less than this
    private const float ChunkMargin = 1f;

    private readonly GL _gl;
    private readonly RenderManager _owner;
    private readonly Dictionary<string, Mesh> _meshes = [];
    private readonly Mesh? _base;
    private readonly Mesh? _lid;
    private readonly Dictionary<string, uint> _skins = new(StringComparer.Ordinal);
    // The skin texture of each chest block by block id; 0 for every other block
    private readonly uint[] _skinOfBlock = [];
    private readonly List<(Matrix4x4 Base, Matrix4x4 Lid, uint Skin, Vector3 LightAt)> _draws = [];

    private TextureUnits Units => _owner.Units;

    internal ChestPasses(GL gl, RenderManager owner)
    {
        _gl = gl;
        _owner = owner;
        if (!ChestModel.IsLoaded) return;

        _meshes = PlayerRig.BuildMeshes(gl, ChestModel.Rig);
        _base = _meshes[ChestModel.BasePart];
        _lid = _meshes[ChestModel.LidPart];

        var types = BlockType.ById;
        _skinOfBlock = new uint[types.Length];
        for (var id = 1; id < types.Length; id++)
        {
            if (types[id]?.Chest is not { } chest) continue;
            if (!_skins.TryGetValue(chest.Skin, out var texture))
            {
                // The block loader already checked the file exists (BlockSchemaParser.ParseChest)
                texture = TextureLoader.LoadFromFile(gl, ChestModel.SkinPath(ContentRegistry.AssetsRoot, chest.Skin),
                    srgb: true);
                _skins[chest.Skin] = texture;
            }
            _skinOfBlock[id] = texture;
        }
    }

    /// <summary>Draws the chests the camera can see into the entity pass.</summary>
    internal void RenderChests(Camera camera, IReadOnlyList<Chunk> chestChunks, ChestLids lids)
    {
        if (camera == null || _base == null || _lid == null) return;
        var viewProj = camera.GetViewProjection();
        if (Gather(chestChunks, lids, new Frustum(viewProj)) == 0) return;

        _owner.WorldShader.Use();
        _owner.BindEntityTextures(mirror: false);
        var bound = 0u;
        foreach (var (baseMatrix, lidMatrix, skin, lightAt) in _draws)
        {
            if (skin != bound)
            {
                Units.BindUnit(0, TextureTarget.Texture2DArray, skin);
                bound = skin;
            }
            // One sample per chest, shared by the base and the lid
            var light = _owner.LightAt(lightAt);
            _owner.SetDrawUniforms(baseMatrix, baseMatrix * viewProj, 1f, light);
            _base.Render();
            _owner.SetDrawUniforms(lidMatrix, lidMatrix * viewProj, 1f, light);
            _lid.Render();
        }
        _owner.BindEntityTextures(mirror: false);
    }

    /// <summary>
    /// Draws the chests inside the near light box as shadow casters, each on its own skin (its texels would
    /// otherwise be alpha-tested against the atlas); the shadow program is already in use and the atlas goes back
    /// on unit 0 after them.
    /// </summary>
    internal void DrawCasters(IReadOnlyList<Chunk> chestChunks, ChestLids lids, Frustum lightFrustum,
        in Matrix4x4 lightViewProj)
    {
        if (_base == null || _lid == null) return;
        if (Gather(chestChunks, lids, lightFrustum) == 0) return;

        var bound = 0u;
        foreach (var (baseMatrix, lidMatrix, skin, _) in _draws)
        {
            if (skin != bound)
            {
                Units.BindUnit(0, TextureTarget.Texture2DArray, skin);
                bound = skin;
            }
            _owner.ShadowShader.SetUniform("uModel", baseMatrix);
            _owner.ShadowShader.SetUniform("uLightMVP", baseMatrix * lightViewProj);
            _base.Render();
            _owner.ShadowShader.SetUniform("uModel", lidMatrix);
            _owner.ShadowShader.SetUniform("uLightMVP", lidMatrix * lightViewProj);
            _lid.Render();
        }
        Units.BindUnit(0, TextureTarget.Texture2DArray, _owner.AtlasTexture);
    }

    // Fills _draws with every chest of the chunks that reach into the frustum: its two part matrices, its skin
    // and the point its light is sampled at (the middle of its cell). A recorded cell that no longer holds a
    // chest (broken a moment ago, its chunk not yet remeshed) is skipped. Returns how many there are.
    private int Gather(IReadOnlyList<Chunk> chestChunks, ChestLids lids, Frustum frustum)
    {
        _draws.Clear();
        var margin = new Vector3(ChunkMargin);
        foreach (var chunk in chestChunks)
        {
            if (chunk.ChestCells is not { } cells) continue;
            var origin = chunk.Position * Chunk.Size;
            if (!frustum.IntersectsAABB(origin - margin, origin + new Vector3(Chunk.Size) + margin)) continue;

            foreach (var index in cells)
            {
                var local = Chunk.LocalOfIdx(index);
                int x = (int)local.X, y = (int)local.Y, z = (int)local.Z;
                var id = chunk.GetLocalBlock(x, y, z);
                if (id >= _skinOfBlock.Length || _skinOfBlock[id] == 0) continue;

                var cell = origin + local;
                var root = ChestModel.Root(cell, chunk.GetLocalState(x, y, z));
                var pose = lids.PoseAt((int)cell.X, (int)cell.Y, (int)cell.Z);
                _draws.Add((ChestModel.PartMatrix(ChestModel.BasePart, root, pose),
                    ChestModel.PartMatrix(ChestModel.LidPart, root, pose), _skinOfBlock[id], cell + new Vector3(0.5f)));
            }
        }
        return _draws.Count;
    }

    /// <summary>Frees the part meshes and the skins.</summary>
    public void Dispose()
    {
        foreach (var mesh in _meshes.Values)
            mesh.Dispose();
        foreach (var texture in _skins.Values)
            _gl.DeleteTexture(texture);
    }
}
