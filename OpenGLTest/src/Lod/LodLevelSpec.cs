using VoxelEngine.WorldGen;

namespace VoxelEngine.Lod;

/// <summary>
/// Parameters for one far-terrain LOD ring. RegionBlocks and RegionRadius must keep
/// CellSize * (RegionBlocks / CellSize) == RegionBlocks with power-of-two sizes.
/// <para><c>ProxyCanopy</c>: whether the ring reads each tree species' <c>lod</c> crown proxy
/// (<see cref="TreeGenerator.TryGetCanopy(int, int, bool, out int, out byte)"/>) instead of the
/// grown canopy. The first ring meets the full-detail chunks and must match what they stamp;
/// every farther ring reads the proxy, so the far sweep never runs tree growth.</para>
/// </summary>
public readonly record struct LodLevelSpec(int CellSize, int RegionBlocks, int RegionRadius, int SampleMargin, int HeightBias,
    bool ProxyCanopy = false)
{
    public int Cells => RegionBlocks / CellSize;
}
