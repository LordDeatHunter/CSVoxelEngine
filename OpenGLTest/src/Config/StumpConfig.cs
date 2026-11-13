namespace VoxelEngine.Config;

/// <summary>
/// The log standing on a tree stump (<see cref="VoxelEngine.Simulation.Stumps.StumpManager"/>): its size, how far
/// from the stump's center it may stand, and the wobble a blow with anything but an axe gives it.
/// </summary>
public static class StumpConfig
{
    /// <summary>Width of the standing log, in blocks.</summary>
    public const float LogWidth = 0.5f;

    /// <summary>Height of the standing log, in blocks.</summary>
    public const float LogHeight = 0.75f;

    /// <summary>Height of a stump's top above its cell floor, in blocks (the stump model is 5 px high).</summary>
    public const float StumpTop = 5f / 16f;

    /// <summary>How far from the stump's center (in blocks, each axis) the log's center may stand: the top is 14 px
    /// wide, so a log <see cref="LogWidth"/> across stays on it.</summary>
    public const float MaxOffset = 14f / 32f - LogWidth / 2f;

    /// <summary>Seconds a wobble rings for.</summary>
    public const float WobbleSeconds = 0.6f;

    /// <summary>Swings in one wobble.</summary>
    public const float WobbleCycles = 3f;

    /// <summary>Largest tilt of the log in a wobble, in radians.</summary>
    public const float WobbleTilt = 0.14f;

    /// <summary>Shortest gap between two wobble-starting blows, in seconds, so a held button wobbles in rhythm.</summary>
    public const float HitGapSeconds = 0.4f;
}
