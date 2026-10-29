namespace VoxelEngine.WorldGen;

/// <summary>
/// One accepted hashed terrain site (a massif or a lone peak): where its center lies, its nominal
/// radius in blocks and the height it adds. <c>default</c> (<see cref="Exists"/> false) means the
/// cell holds no site, which is what a site cache stores for a rejected or unrolled cell.
/// <see cref="OnIsland"/>: the site's center has a pre-island ocean weight above 0
/// (<see cref="BiomeClassifier.PreIslandOceanWeight"/>), so it stands on an island, and its
/// contribution is scaled by the island land weight (<see cref="TerrainIslands.LandWeight(float, float, float)"/>).
/// </summary>
internal readonly record struct TerrainSite(bool Exists, float CenterX, float CenterZ, float Radius, float Height,
    bool OnIsland = false);
