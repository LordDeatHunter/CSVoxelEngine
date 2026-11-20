using System;

namespace VoxelEngine.Lod;

/// <summary>Inclusive-low, exclusive-high bounds of one finite LOD tree prism.</summary>
internal readonly record struct LodProxyBox(float MinX, float MinY, float MinZ,
    float MaxX, float MaxY, float MaxZ)
{
    internal bool HasVolume => MaxX > MinX && MaxY > MinY && MaxZ > MinZ;

    internal LodProxyBox ClipXZ(float minX, float minZ, float maxX, float maxZ) =>
        new(MathF.Max(MinX, minX), MinY, MathF.Max(MinZ, minZ),
            MathF.Min(MaxX, maxX), MaxY, MathF.Min(MaxZ, maxZ));
}
