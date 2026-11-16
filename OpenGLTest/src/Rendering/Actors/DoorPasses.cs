using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Content.Models;
using VoxelEngine.Data;
using VoxelEngine.Graphics;
using VoxelEngine.Interaction;
using VoxelEngine.Player;
using VoxelEngine.Simulation.Doors;
using VoxelEngine.UI;

namespace VoxelEngine.Rendering.Actors;

/// <summary>
/// The swinging doors' draws. A door that is mid-swing has no geometry in its chunk's mesh (its cells are in
/// <c>ChunkManager.SwingingCells</c>): both halves are drawn here as one panel from the block's own shut models,
/// turned about the hinge by <see cref="DoorSwingShape.PanelMatrix"/>, for the entity pass and the near shadow
/// cascade. One mesh per door block and hinge side, built on first use. Render thread only.
/// </summary>
internal sealed class DoorPasses(GL gl, RenderManager owner) : IDisposable
{
    private readonly Dictionary<(ushort LowerId, bool HingeRight), Mesh?> _meshes = [];

    /// <summary>Draws the swinging doors into the entity pass.</summary>
    internal void RenderDoors(Camera camera, IReadOnlyList<DoorSwing> swings)
    {
        if (camera == null || swings.Count == 0) return;
        var viewProj = camera.GetViewProjection();
        owner.WorldShader.Use();
        owner.BindEntityTextures(mirror: false);
        foreach (var swing in swings)
        {
            if (PanelOf(swing) is not { } mesh) continue;
            var model = DoorSwingShape.PanelMatrix(BlockType.ById[swing.LowerId]!, swing.Cell, swing.State, swing.Open);
            // One sample for the whole door, where its two halves meet
            owner.SetDrawUniforms(model, model * viewProj, 1f, owner.LightAt(swing.Cell + new Vector3(0.5f, 1f, 0.5f)));
            mesh.Render();
        }
    }

    /// <summary>Draws the swinging doors inside the near light box as shadow casters; the shadow program is
    /// already in use with the atlas on unit 0.</summary>
    internal void DrawCasters(IReadOnlyList<DoorSwing> swings, Frustum lightFrustum, in Matrix4x4 lightViewProj)
    {
        foreach (var swing in swings)
        {
            if (!lightFrustum.IntersectsAABB(swing.Cell, swing.Cell + new Vector3(1f, 2f, 1f))) continue;
            if (PanelOf(swing) is not { } mesh) continue;
            var model = DoorSwingShape.PanelMatrix(BlockType.ById[swing.LowerId]!, swing.Cell, swing.State, swing.Open);
            owner.ShadowShader.SetUniform("uModel", model);
            owner.ShadowShader.SetUniform("uLightMVP", model * lightViewProj);
            mesh.Render();
        }
    }

    // Both halves' shut models (the mirrored ones for a right-hand hinge) in the lower cell's own space, the upper
    // half one cell up
    private Mesh? PanelOf(in DoorSwing swing)
    {
        var key = (swing.LowerId, DoorState.HingeRight(swing.State));
        if (_meshes.TryGetValue(key, out var mesh)) return mesh;

        var shape = BlockType.ShapeIndex(DoorState.Encode(0, false, key.Item2), DoorState.HingedShapeCount);
        if (BlockType.ById[swing.LowerId] is { RotatedModels: { } lower } type
            && BlockType.ById[type.UpperHalfId]?.RotatedModels is { } upper)
        {
            var quads = new List<BakedQuad>(lower[shape].Quads);
            foreach (var quad in upper[shape].Quads)
                quads.Add(quad with { Positions = [.. Array.ConvertAll(quad.Positions, p => p + Vector3.UnitY)] });
            mesh = Primitives.CreateBlockModel(gl, quads, center: Vector3.Zero);
        }

        _meshes[key] = mesh;
        return mesh;
    }

    /// <summary>Frees the panel meshes.</summary>
    public void Dispose()
    {
        foreach (var mesh in _meshes.Values)
            mesh?.Dispose();
    }
}
