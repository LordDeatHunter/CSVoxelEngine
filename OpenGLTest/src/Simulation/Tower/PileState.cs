namespace VoxelEngine.Simulation.Tower;

/// <summary>
/// One skeleton pile of a tower floor: its cell, how many waves it still raises, the time until it may raise again and
/// whether its depleted block has been cleared from the world. Main thread only.
/// </summary>
public sealed class PileState((int X, int Y, int Z) cell, int wavesLeft)
{
    /// <summary>The pile's world cell.</summary>
    public (int X, int Y, int Z) Cell { get; } = cell;

    /// <summary>The waves it still raises; 0 once depleted (saved).</summary>
    public int WavesLeft { get; set; } = wavesLeft;

    /// <summary>Seconds until it may raise a wave (or owed skeletons) again; it may at 0 or below.</summary>
    public float Cooldown { get; set; }

    /// <summary>Whether a chest opened nearby has forced it awake: its next wave rises at once, ignoring its trigger
    /// range, cooldown and the near-pile skeleton limit. Cleared once a wave rises; never saved.</summary>
    public bool Alarmed { get; set; }

    /// <summary>Whether its block has been cleared from the world after it depleted (or was already gone).</summary>
    public bool Removed { get; set; }

    /// <summary>Whether it has no wave left.</summary>
    public bool Depleted => WavesLeft <= 0;
}
