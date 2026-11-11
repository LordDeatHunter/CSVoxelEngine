namespace VoxelEngine.Simulation.Fluids;

/// <summary>
/// Reads one cell's block id and state byte for <see cref="FluidFlow"/>. Returns false when
/// the cell is not available (an unloaded chunk), which the flow treats like a dam. Implemented
/// by small structs so the generic flow code is specialized per reader and never boxes.
/// </summary>
internal interface IFluidCellReader
{
    bool TryGetCell(int x, int y, int z, out ushort id, out byte state);
}
