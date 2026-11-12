using System;
using System.Collections.Generic;
using System.Numerics;

namespace VoxelEngine.Particles;

/// <summary>
/// The canopy a chop shakes loose: the leaf cells above and around the wood cell an axe hit, a bounded
/// box read once per hit, thinned to a few evenly spread cells so a thick crown shakes a handful of
/// leaves, never hundreds. GL-free: the caller supplies what counts as a leaf, and feeds the cells to
/// <see cref="LeafParticleSystem.BurstCells"/>.
/// </summary>
public static class TreeShudder
{
    /// <summary>
    /// Fills <paramref name="into"/> (cleared first) with up to <paramref name="maxCells"/> leaf cells
    /// found within <paramref name="reach"/> blocks sideways of <paramref name="woodCell"/> and from its
    /// level up <paramref name="height"/> blocks, spread evenly through the ones found.
    /// </summary>
    public static void Gather(Vector3 woodCell, Func<int, int, int, bool> isLeaf, List<Vector3> into,
        int maxCells, int reach, int height)
    {
        into.Clear();
        if (maxCells <= 0) return;
        int cx = (int)MathF.Floor(woodCell.X), cy = (int)MathF.Floor(woodCell.Y), cz = (int)MathF.Floor(woodCell.Z);
        for (var dy = 0; dy <= height; dy++)
        for (var dx = -reach; dx <= reach; dx++)
        for (var dz = -reach; dz <= reach; dz++)
            if (isLeaf(cx + dx, cy + dy, cz + dz))
                into.Add(new Vector3(cx + dx, cy + dy, cz + dz));

        // Thin to evenly spaced picks through the scan order, in place (a pick's index is never below its slot)
        var count = into.Count;
        if (count <= maxCells) return;
        for (var i = 0; i < maxCells; i++)
            into[i] = into[(int)((long)i * count / maxCells)];
        into.RemoveRange(maxCells, count - maxCells);
    }
}
