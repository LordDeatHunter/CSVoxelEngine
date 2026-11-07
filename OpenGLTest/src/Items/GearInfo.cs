using System.Collections.Generic;

namespace VoxelEngine.Items;

/// <summary>
/// What a piece of gear is, compiled from its item JSON's <c>equip</c> object: the slot it fits, its fixed stat
/// changes, the set it counts toward, whether it is drawn on the body, the effects it makes its wearer immune to
/// and its passive triggers.
/// </summary>
/// <param name="Slot">The slot kind it fits.</param>
/// <param name="Modifiers">Its fixed stat changes (armor included).</param>
/// <param name="SetName">The set it belongs to, or null.</param>
/// <param name="Visible">Whether it is drawn on the player's body.</param>
/// <param name="Immune">Status effect names it blocks.</param>
/// <param name="Passives">Its passive triggers.</param>
public sealed record GearInfo(
    GearSlot Slot,
    IReadOnlyList<GearModifier> Modifiers,
    string? SetName,
    bool Visible,
    IReadOnlySet<string> Immune,
    IReadOnlyList<GearPassive> Passives);
