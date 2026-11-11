namespace VoxelEngine.WorldGen.Hell;

/// <summary>The open field, the row rules and the biome lookups the hell rules read
/// (<see cref="HellGenerator"/>'s classification and <see cref="HellFlora"/>): computed on the spot
/// by the point query, or from the stamp's per-chunk caches, always to the same values.</summary>
internal interface IHellField
{
    bool IsOpen(int x, int y, int z);
    bool CanWearSkin(int y);
    bool InPool(int y);
    HellBiome Biome(int x, int layer, int z);
    bool PoolIsLava(int x, int layer, int z);
}
