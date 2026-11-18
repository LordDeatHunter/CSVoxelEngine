using VoxelEngine.WorldGen.Volcanoes;

namespace VoxelEngine.WorldGen;

/// <summary>Whole-footprint exclusion shared by existing surface writers. The erg rule is
/// unchanged; Dynamic volcanoes additionally reserve only their owned caldera guard.</summary>
internal static class FeatureExclusion
{
    internal static bool Intersects(int minX, int minZ, int maxX, int maxZ) =>
        TerrainErg.Intersects(minX, minZ, maxX, maxZ)
        || VolcanoSites.IntersectsGuard(minX, minZ, maxX, maxZ);

    internal static bool IntersectsDisc(int x, int z, int radius) =>
        TerrainErg.IntersectsDisc(x, z, radius)
        || VolcanoSites.IntersectsGuardDisc(x, z, radius);
}
