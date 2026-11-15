namespace VoxelEngine.Networking;

/// <summary>
/// The swing a player just made, as the server rebroadcasts it in each player list entry
/// (with a counter that moves on every new one), so other clients can play the same animation on
/// that player's model. Purely cosmetic: the intent itself is judged on its own rules.
/// </summary>
public enum NetPlayerAction : byte
{
    None = 0,
    Punch = 1,
    Slice = 2,
    SliceReturn = 3,
    Throw = 4,
    SwordCut = 5,
    SwordBackCut = 6,
    SwordChop = 7,
    /// <summary>A staff cast's gesture: the staff raised and thrust forward (PlayerModel.Cast).</summary>
    Cast = 8,
    /// <summary>A throw released from the left hand: the same swing on the other arm (added without a bump, like
    /// <see cref="Cast"/>: an older client plays nothing for an action it does not know).</summary>
    ThrowOffhand = 9,
    /// <summary>A right-click placement or use the left hand took: the jab on the other arm.</summary>
    PunchOffhand = 10,
}
