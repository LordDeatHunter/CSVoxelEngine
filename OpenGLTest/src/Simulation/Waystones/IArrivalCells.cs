namespace VoxelEngine.Simulation.Waystones;

/// <summary>The cell reads the waystone arrival search runs over (<see cref="WaystoneArrival.Find"/>): the live chunks
/// (<see cref="ChunkArrivalCells"/>) or a test's hand-built cells.</summary>
public interface IArrivalCells
{
    /// <summary>What the cell at (x, y, z) is to a body arriving there.</summary>
    ArrivalCell At(int x, int y, int z);
}
