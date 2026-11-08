using System;

namespace VoxelEngine.Items;

/// <summary>
/// The abilities a piece of gear may grant its wearer (an item's <c>equip.abilities</c>), as flags so the worn set's
/// union is one value. Parsed and cached now; AC-3 and AC-4 wire the consumers.
/// </summary>
[Flags]
public enum GearAbility : byte
{
    /// <summary>No ability.</summary>
    None = 0,

    /// <summary>One free jump in the air, reset on landing.</summary>
    AirJump = 1,

    /// <summary>No fall damage.</summary>
    NoFallDamage = 2,

    /// <summary>A longer breath under water.</summary>
    ExtraAir = 4,

    /// <summary>A wider pickup radius for item drops.</summary>
    Magnet = 8,
}
