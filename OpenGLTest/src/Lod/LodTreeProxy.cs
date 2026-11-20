using System;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Shape;

namespace VoxelEngine.Lod;

/// <summary>A finite render-only stand-in for one rooted tree. Coordinates are world block edges.</summary>
internal readonly record struct LodTreeProxy(
    int RootX, int RootZ, int BaseY, int CrownBottom, int CrownTop,
    int MinX, int MaxX, int MinZ, int MaxZ, int TrunkWidth,
    ushort WoodId, ushort LeavesId, LodCrownProfile Profile)
{
    internal bool HasCrown => LeavesId != 0 && CrownTop > CrownBottom;

    internal static LodTreeProxy FromGrown(TreeBatches.TreeShape shape)
    {
        var minX = int.MaxValue;
        var maxX = int.MinValue;
        var minZ = int.MaxValue;
        var maxZ = int.MinValue;
        var minY = int.MaxValue;
        var maxY = int.MinValue;
        for (var i = shape.WoodCount; i < shape.Cells.Length; i++)
        {
            var c = shape.Cells[i];
            minX = Math.Min(minX, c.X);
            maxX = Math.Max(maxX, c.X + 1);
            minZ = Math.Min(minZ, c.Z);
            maxZ = Math.Max(maxZ, c.Z + 1);
            minY = Math.Min(minY, c.Y);
            maxY = Math.Max(maxY, c.Y + 1);
        }

        var s = shape.Species;
        if (minY == int.MaxValue)
            return new LodTreeProxy(shape.X, shape.Z, shape.BaseY, shape.MaxY + 1, shape.MaxY + 1,
                shape.X, shape.X + 1, shape.Z, shape.Z + 1, Math.Max(1, s.MaxPx / 16),
                s.WoodId, 0, s.LodProfile);
        return new LodTreeProxy(shape.X, shape.Z, shape.BaseY, minY, maxY,
            minX, maxX, minZ, maxZ, Math.Max(1, s.MaxPx / 16), s.WoodId, s.LeavesId, s.LodProfile);
    }

    internal static LodTreeProxy FromFar(TreeSpecies species, int x, int z, int baseY)
    {
        // Historical far canopy omitted leafless species. Their old lod values were
        // never a wood-height contract (cactus explicitly used placeholder metadata).
        if (!species.HasLeaves)
            throw new ArgumentException("Leafless species have no far-tree proxy.", nameof(species));
        var height = TreeGenerator.CrownHeightOf(species, x, z);
        var top = baseY + height + 1;
        var bottom = Math.Max(baseY + 1, top - Math.Max(2, (height + 2) / 3));
        var r = species.CrownRadius;
        return new LodTreeProxy(x, z, baseY, bottom, top,
            x - r, x + r + 1, z - r, z + r + 1, Math.Max(1, species.MaxPx / 16),
            species.WoodId, species.LeavesId, species.LodProfile);
    }

    internal LodProxyBox Trunk()
    {
        var width = Math.Max(1, TrunkWidth);
        var half = width * 0.5f;
        return new LodProxyBox(RootX + 0.5f - half, BaseY, RootZ + 0.5f - half,
            RootX + 0.5f + half, Math.Max(BaseY + 1, CrownBottom), RootZ + 0.5f + half);
    }

    internal LodProxyBox Crown() => new(MinX, CrownBottom, MinZ, MaxX,
        Profile == LodCrownProfile.Column ? CrownTop : CrownBottom + Math.Max(1, (CrownTop - CrownBottom) / 2), MaxZ);

    // A second, narrower cap gives cones and domes a silhouette without growing far trees.
    // Root parity is a cheap stable two-variant choice; it does not consume a placement roll.
    internal LodProxyBox Cap()
    {
        var middle = CrownBottom + Math.Max(1, (CrownTop - CrownBottom) / 2);
        var inset = Profile == LodCrownProfile.Column ? 0f
            : Profile == LodCrownProfile.Cone ? 0.32f : 0.18f;
        if (Profile != LodCrownProfile.Column && ((RootX ^ RootZ) & 1) != 0)
            inset += 0.04f;
        var dx = (MaxX - MinX) * inset;
        var dz = (MaxZ - MinZ) * inset;
        return new LodProxyBox(MinX + dx, middle, MinZ + dz,
            MaxX - dx, CrownTop, MaxZ - dz);
    }
}
