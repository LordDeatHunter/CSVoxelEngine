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

    /// <summary>Sideways speed (blocks per second, at most half of it each way) the lumber of a split log is thrown out at.</summary>
    public const float SplitScatter = 3f;

    /// <summary>Upward speed the lumber of a split log is thrown out at.</summary>
    public const float SplitLift = 2.5f;

    /// <summary>Axe blows a log takes to split on a stump unless its block says otherwise (a hollow log takes half).</summary>
    public const int DefaultSplitHits = 4;

    /// <summary>How far each half of a log being split has moved aside, in blocks, per blow taken.</summary>
    public const float SplitGapPerHit = 0.045f;

    /// <summary>How far each half leans away from the other, in radians, per blow taken.</summary>
    public const float SplitLeanPerHit = 0.05f;

    /// <summary>Seconds the axe's overhead swing takes from raised to split (<c>SwordSwing.AxeSplit</c>): a slow blow.</summary>
    public const float SplitSwingSeconds = 1.25f;
}
