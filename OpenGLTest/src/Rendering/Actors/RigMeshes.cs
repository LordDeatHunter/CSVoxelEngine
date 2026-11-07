using System;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Entities;
using VoxelEngine.Graphics;
using VoxelEngine.Player;

namespace VoxelEngine.Rendering.Actors;

/// <summary>
/// One clip-animated entity kind's GL side: its rig, one mesh per Blockbench group and its skin,
/// built once from the content load's parse (<see cref="EntityType.Model"/>). The part names are
/// every rig group, ordinal-sorted, so a group the file adds beyond the required ones draws too;
/// <see cref="Meshes"/> is index-aligned with them. Render thread only.
/// </summary>
internal sealed class RigMeshes : IDisposable
{
    private readonly GL _gl;

    private RigMeshes(GL gl, PlayerRig rig, string[] partNames, Mesh[] meshes, uint texture)
    {
        _gl = gl;
        Rig = rig;
        PartNames = partNames;
        Meshes = meshes;
        Texture = texture;
    }

    public PlayerRig Rig { get; }
    public string[] PartNames { get; }
    public Mesh[] Meshes { get; }
    public uint Texture { get; }

    /// <summary>Builds the part meshes and uploads the skin of <paramref name="type"/>.</summary>
    public static RigMeshes Build(GL gl, EntityType type)
    {
        var rig = PlayerRig.FromBbmodel(type.Model);
        var byName = PlayerRig.BuildMeshes(gl, rig);
        var names = new string[byName.Count];
        byName.Keys.CopyTo(names, 0);
        Array.Sort(names, StringComparer.Ordinal);
        var meshes = new Mesh[names.Length];
        for (var i = 0; i < names.Length; i++)
            meshes[i] = byName[names[i]];
        var texture = TextureLoader.Upload(gl, rig.TexturePixels, rig.TextureWidth, rig.TextureHeight, srgb: true);
        return new RigMeshes(gl, rig, names, meshes, texture);
    }

    /// <summary>The world matrix of part <paramref name="index"/> of <paramref name="mob"/>, posed by its
    /// animator about its feet, before its body effect.</summary>
    public Matrix4x4 PartMatrix(RiggedMob mob, int index) =>
        mob.Animation.GetPartMatrix(PartNames[index], Rig, mob.Feet, mob.Facing);

    public void Dispose()
    {
        foreach (var mesh in Meshes)
            mesh.Dispose();
        _gl.DeleteTexture(Texture);
    }
}
