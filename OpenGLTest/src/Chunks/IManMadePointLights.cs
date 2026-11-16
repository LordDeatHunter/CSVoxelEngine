using System.Collections.Generic;

namespace VoxelEngine.Chunks;

/// <summary>
/// The man-made lights that shine as point lights instead of voxel block light (fueled lanterns, lit campfires,
/// kilns and bloomeries), which a world registers on its <see cref="ChunkManager.ManMadePointLights"/> so the mob
/// spawn searches can keep clear of them like they keep clear of a torch.
/// </summary>
public interface IManMadePointLights
{
    /// <summary>Adds the cell of every such light burning right now to <paramref name="cells"/>.</summary>
    void AppendLitCells(List<(int X, int Y, int Z)> cells);
}
