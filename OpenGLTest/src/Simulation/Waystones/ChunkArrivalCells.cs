using VoxelEngine.Chunks;

namespace VoxelEngine.Simulation.Waystones;

/// <summary>The live chunks as the waystone arrival search reads them: an unloaded cell is
/// <see cref="ArrivalCell.Unloaded"/>, a fluid <see cref="ArrivalCell.Blocked"/>, a solid block
/// <see cref="ArrivalCell.Solid"/>, anything else <see cref="ArrivalCell.Open"/>.</summary>
public sealed class ChunkArrivalCells(ChunkManager chunks) : IArrivalCells
{
    public ArrivalCell At(int x, int y, int z)
    {
        if (!chunks.TryGetCell(x, y, z, out var id, out _)) return ArrivalCell.Unloaded;
        if (BlockType.IsFluid(id)) return ArrivalCell.Blocked;
        return BlockType.IsSolid(id) ? ArrivalCell.Solid : ArrivalCell.Open;
    }
}
