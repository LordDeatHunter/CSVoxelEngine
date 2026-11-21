namespace VoxelEngine.WorldGen.Trees.Shape;

/// <summary>
/// A worldgen tree's far crown must fit inside the already validated grown horizontal
/// reach. This predicate is pure; the profile-table compiler applies it only to eligible
/// leafy worldgen species, leaving manually grown species and Legacy membership alone.
/// </summary>
internal static class TreeProxyReach
{
    internal static bool FitsGrownReach(int crownRadius, int grownHorizontal) =>
        crownRadius >= 0 && grownHorizontal >= 0 && crownRadius <= grownHorizontal;
}
