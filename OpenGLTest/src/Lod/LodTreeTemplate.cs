using System;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Lod;

/// <summary>
/// Reusable box topology for a trunk, broad crown and cap. Both base and tint layers
/// exist once in this template; a future draw skips a layer whose material face is NoTile.
/// Geometry is never copied per root. All actual bounds come from a validated instance.
/// </summary>
internal static class LodTreeTemplate
{
    internal const int Parts = 3;
    internal const int Faces = 6;
    internal const int Layers = 2;
    internal const int Vertices = Parts * Faces * Layers * 4;
    internal const int Indices = Parts * Faces * Layers * 6;

    internal readonly record struct Vertex(Vector3 UnitPosition, byte Part, byte Face, byte Layer);

    internal static (Vertex[] Vertices, ushort[] Indices) Build()
    {
        var vertices = new Vertex[Vertices];
        var indices = new ushort[Indices];
        var v = 0;
        var n = 0;
        for (byte part = 0; part < Parts; part++)
        for (byte face = 0; face < Faces; face++)
        for (byte layer = 0; layer < Layers; layer++)
        {
            var corner = Chunk.CubeFaces[face].face;
            for (var k = 0; k < 4; k++) vertices[v + k] = new Vertex(corner[k], part, face, layer);
            indices[n++] = (ushort)v;
            indices[n++] = (ushort)(v + 1);
            indices[n++] = (ushort)(v + 2);
            indices[n++] = (ushort)v;
            indices[n++] = (ushort)(v + 2);
            indices[n++] = (ushort)(v + 3);
            v += 4;
        }
        return (vertices, indices);
    }

    /// <summary>Only fully materialized parts participate; absent caps/trunks do not leave fragments.</summary>
    internal static bool TryPart(LodTreeInstance instance, int part, int originX, int originZ,
        out LodProxyBox box, out int material)
    {
        box = default;
        material = LodTreeInstance.NoMaterial;
        if (part is < 0 or >= Parts) return false;
        var tree = instance.ToRegionProxy(originX, originZ);
        switch (part)
        {
            case 0 when instance.HasTrunk:
                box = tree.Trunk();
                material = instance.WoodMaterial;
                break;
            case 1 when instance.HasCrown:
                box = tree.Crown();
                material = instance.LeafMaterial;
                break;
            case 2 when instance.HasCrown && tree.Profile != VoxelEngine.WorldGen.Trees.Shape.LodCrownProfile.Column:
                box = tree.Cap();
                material = instance.LeafMaterial;
                break;
        }
        return material >= 0 && box.HasVolume;
    }
}
