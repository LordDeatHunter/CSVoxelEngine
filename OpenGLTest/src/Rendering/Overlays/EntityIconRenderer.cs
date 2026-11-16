using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Data;
using VoxelEngine.Entities;
using VoxelEngine.Player;
using VoxelEngine.Rendering.Actors;
using VoxelEngine.UI.Quests;

namespace VoxelEngine.Rendering.Overlays;

/// <summary>
/// An entity drawn as a UI icon (a quest whose icon is an entity, DevStudio's icon grid): its model in its rest pose, laid
/// out by <see cref="EntityIconLayout"/> and drawn through the UI shader like a block icon, depth-tested among its own parts.
/// Each kind's meshes, skin and layout are built on first use and kept until <see cref="Dispose"/>. Render thread only.
/// </summary>
internal sealed class EntityIconRenderer(GL gl, UIRenderer ui) : IDisposable
{
    private sealed record Icon(RigMeshes Rig, Matrix4x4[] Parts, Matrix4x4 Fit);

    private readonly Dictionary<string, Icon?> _icons = new(StringComparer.Ordinal);

    /// <summary>Draws the entity with file name <paramref name="entityName"/> to fit a <paramref name="size"/> square
    /// centered on <paramref name="center"/>; an unknown entity draws nothing.</summary>
    internal void Draw(Vector2 center, float size, string entityName)
    {
        if (IconOf(entityName) is not { } icon) return;
        gl.Enable(EnableCap.DepthTest);
        ui.EnsureDepthCleared();
        var screen = icon.Fit * Matrix4x4.CreateScale(size) * UIRenderer.FlipToScreen(center);
        for (var i = 0; i < icon.Parts.Length; i++)
            ui.DrawMesh(icon.Rig.Meshes[i], icon.Parts[i] * screen, Rgba.White, icon.Rig.Texture, srgb: true);
        gl.Disable(EnableCap.DepthTest);
    }

    private Icon? IconOf(string entityName)
    {
        if (_icons.TryGetValue(entityName, out var cached)) return cached;
        Icon? icon = null;
        if (EntityRegistry.TryGet(entityName) is { } type)
        {
            var rig = RigMeshes.Build(gl, type);
            // The rest pose: a fresh animator, at the origin, facing its native -Z
            var animator = new EntityAnimator(type.Model.Animations);
            var parts = new Matrix4x4[rig.PartNames.Length];
            var cubes = new List<(Matrix4x4, IReadOnlyList<PlayerRig.RigCube>)>();
            for (var i = 0; i < parts.Length; i++)
            {
                parts[i] = animator.GetPartMatrix(rig.PartNames[i], rig.Rig, Vector3.Zero, 0f);
                if (rig.Rig.Parts.TryGetValue(rig.PartNames[i], out var part)) cubes.Add((parts[i], part.Cubes));
            }
            icon = new Icon(rig, parts, EntityIconLayout.Fit(cubes));
        }
        _icons[entityName] = icon;
        return icon;
    }

    public void Dispose()
    {
        foreach (var icon in _icons.Values) icon?.Rig.Dispose();
        _icons.Clear();
    }
}
