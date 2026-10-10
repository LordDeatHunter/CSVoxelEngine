namespace silkgltriangle;

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
}
