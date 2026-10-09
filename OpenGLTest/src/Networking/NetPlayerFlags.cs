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
}
