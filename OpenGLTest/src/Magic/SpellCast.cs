using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.Spells;
using VoxelEngine.Items;
using VoxelEngine.Player;

namespace VoxelEngine.Magic;

/// <summary>
/// One accepted cast, as the effect hook receives it: who cast it, with which staff and slot, the
/// resolved parts and their compiled stats (the mana already billed), and where it was aimed from.
/// <paramref name="Eye"/> and <paramref name="Direction"/> are copied out of the caster's context,
/// because a server reuses one <see cref="PlayerContext"/> per player and moves it for every intent.
/// </summary>
/// <param name="Caster">The player the cast ran for.</param>
/// <param name="Staff">The staff in the caster's hand.</param>
/// <param name="Slot">The staff slot that was cast.</param>
/// <param name="Parts">The slot's spell parts, in recipe order.</param>
/// <param name="Stats">What the recipe compiled to against the staff's tags.</param>
/// <param name="Eye">The eye the cast left from.</param>
/// <param name="Direction">The unit aim the cast left along.</param>
public readonly record struct SpellCast(PlayerContext Caster, StaffItem Staff, int Slot,
    IReadOnlyList<SpellPart> Parts, SpellStats Stats, Vector3 Eye, Vector3 Direction);
