namespace VoxelEngine.Entities;

/// <summary>How an entity kind treats a player (its file's <c>attitude</c>): what colors its pin on the map.</summary>
public enum EntityAttitude : byte
{
    /// <summary>Attacks on sight; the default.</summary>
    Hostile,

    /// <summary>Leaves a player alone until provoked, or flees.</summary>
    Neutral,
}
