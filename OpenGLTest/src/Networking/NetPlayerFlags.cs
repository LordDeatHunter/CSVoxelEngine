using System;

namespace silkgltriangle;

/// <summary>
/// The flags byte of a <see cref="NetMessage.PlayerState"/> (and of each player list entry): the
/// modes the client is in. The server treats them as a request; see
/// <see cref="ServerPlayer.Creative"/>.
/// </summary>
[Flags]
public enum NetPlayerFlags : byte
{
    None = 0,
    Creative = 1,
    Flying = 2,

    // Cosmetic movement state, reported so other clients can pose this player's model (a crouch,
    // a swim stroke, a run cycle, a jump). The server rebroadcasts them untrusted and never acts on them.
    Sneaking = 4,
    Swimming = 8,
    Sprinting = 16,
    Airborne = 32,
    // At rest at the top of deep water (WaterPhase.Float, always with Swimming): the surface-float
    // pose. Like the rest it is rebroadcast as the raw byte, which no reader rejects, so an older
    // client simply ignores it and poses the swim.
    Floating = 64,
}
