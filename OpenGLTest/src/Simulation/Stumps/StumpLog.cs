namespace VoxelEngine.Simulation.Stumps;

/// <summary>The log standing on one stump: which log, where on the stump's top it stands (offsets from the cell
/// center, in blocks), how it is turned and how long its wobble still rings.</summary>
public sealed class StumpLog
{
    /// <summary>The log block it was made from, so a pick-up or a broken stump gives that log back.</summary>
    public ushort LogId;

    /// <summary>Offset from the cell center along x, in blocks.</summary>
    public float OffsetX;

    /// <summary>Offset from the cell center along z, in blocks.</summary>
    public float OffsetZ;

    /// <summary>Yaw about the vertical, in radians.</summary>
    public float Yaw;

    /// <summary>Axe blows the log has taken toward its split (0 = whole).</summary>
    public int SplitHits;

    /// <summary>Seconds of wobble left (0 = still).</summary>
    public float WobbleLeft;
}
