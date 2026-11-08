using System;

namespace VoxelEngine.Items;

/// <summary>
/// The abilities a piece of gear may grant its wearer (an item's <c>equip.abilities</c>), as flags so the worn set's
/// union is one value, read through the cached <c>Equipment.Has</c>. The consumers: <see cref="AirJump"/> in
/// <c>Camera.GearAirJump</c> and <c>ServerPlayer.AirJumps</c>; <see cref="NoFallDamage"/> in
/// <c>PlayerHealth.NegatesFallDamage</c> (inside its <c>TakeDamage</c>) and <c>ServerPlayer.ObserveAcceptedState</c>;
/// <see cref="ExtraAir"/> in <c>Breath.SetMaxAir</c> from <c>PlayerHealth.ApplyGear</c> (<c>GearConfig.ExtraAirFactor</c>);
/// <see cref="Magnet"/> in <c>DropCollector.Range</c> from <c>DropPickup</c> (<c>GearConfig.MagnetRangeFactor</c>).
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
