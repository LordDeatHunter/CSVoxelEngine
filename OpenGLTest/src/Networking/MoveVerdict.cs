namespace VoxelEngine.Networking;

/// <summary>What <see cref="MovementRules.Judge"/> made of one player state's position.</summary>
public enum MoveVerdict
{
    /// <summary>The position is taken as reported.</summary>
    Accepted,

    /// <summary>
    /// Faster than the speed cap: the position advances toward the report by the allowed distance
    /// only (<see cref="NetValidation.AcceptMove"/>), converging on the client without a correction.
    /// </summary>
    Clamped,

    /// <summary>
    /// Illegal (into a solid cell, too steep a climb or a hover without flight): the player stays
    /// where it was and the client is snapped back there.
    /// </summary>
    Rejected,
}
