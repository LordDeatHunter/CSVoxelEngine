using System.Collections.Generic;

namespace VoxelEngine.Items;

/// <summary>An armor set: its display name, its bonuses by piece count and the items that belong to it.</summary>
/// <param name="Name">The file name under <c>assets/data/sets</c>, which items name in <c>equip.set</c>.</param>
/// <param name="DisplayName">What the tooltip shows.</param>
/// <param name="Bonuses">The bonuses, ascending by piece count.</param>
/// <param name="Members">Item ids of the pieces that name this set.</param>
public sealed record GearSet(string Name, string DisplayName, IReadOnlyList<GearSetBonus> Bonuses,
    IReadOnlyList<byte> Members);
