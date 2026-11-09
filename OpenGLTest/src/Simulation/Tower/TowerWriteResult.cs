namespace VoxelEngine.Simulation.Tower;

/// <summary>What one <see cref="TowerBlockWriter.TryClear"/> call came to.</summary>
public enum TowerWriteResult
{
    /// <summary>The cell held the expected block and is air now.</summary>
    Written,

    /// <summary>The cell is loaded but no longer holds the expected block; nothing was written.</summary>
    Gone,

    /// <summary>The cell's chunk is not loaded; nothing was written, so try it again later.</summary>
    Unloaded,

    /// <summary>The frame's write budget is spent; nothing was written, so try it again next frame.</summary>
    Capped,
}
