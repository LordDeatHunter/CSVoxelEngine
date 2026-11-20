using System;

namespace VoxelEngine.Lod;

/// <summary>
/// Immutable logical tree layer of a region build. Instances share materials and the two
/// explicit tint lattices. Source ownership and proxy Y intervals are one entry per 16-block
/// tile; an empty interval means that tile has no tree even if its ground is present.
/// </summary>
public sealed class LodTreeBuild
{
    public static LodTreeBuild Empty { get; } = new([], [], null, [], [], []);

    internal LodTreeInstance[] Instances { get; }
    internal LodTreeMaterial[] Materials { get; }
    internal LodTreeTintLattice? Tint { get; }
    internal ulong[] GeneratedTiles { get; }
    internal int[] ProxyYMin { get; }
    internal int[] ProxyYMax { get; }

    internal int Count => Instances.Length;

    internal LodTreeBuild(LodTreeInstance[] instances, LodTreeMaterial[] materials,
        LodTreeTintLattice? tint, ulong[] generatedTiles, int[] proxyYMin, int[] proxyYMax)
    {
        if (proxyYMin.Length != proxyYMax.Length ||
            (proxyYMin.Length != 0 && proxyYMin.Length != 16 * 16 &&
             proxyYMin.Length != 8 * 8 && proxyYMin.Length != 4 * 4) ||
            generatedTiles.Length != (proxyYMin.Length + 63) / 64)
            throw new ArgumentException("Tree coverage must match the region's 16-block tiles.");
        Instances = instances;
        Materials = materials;
        Tint = tint;
        GeneratedTiles = generatedTiles;
        ProxyYMin = proxyYMin;
        ProxyYMax = proxyYMax;
    }
}
