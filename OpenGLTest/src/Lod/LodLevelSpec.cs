using VoxelEngine.WorldGen;

namespace VoxelEngine.Lod;

/// <summary>
/// Parameters for one far-terrain LOD ring. RegionBlocks and RegionRadius must keep
/// CellSize * (RegionBlocks / CellSize) == RegionBlocks with power-of-two sizes.
/// <para><c>ProxyCanopy</c>: whether separate finite tree prisms use the species' cheap
/// <c>lod</c> bounds instead of batch-grown leaf bounds. The first ring meets the full-detail
/// chunks; farther rings never run tree growth.</para>
/// </summary>
public readonly record struct LodLevelSpec(int CellSize, int RegionBlocks, int RegionRadius, int SampleMargin, int HeightBias,
    bool ProxyCanopy = false)
{
    public int Cells => RegionBlocks / CellSize;
}
