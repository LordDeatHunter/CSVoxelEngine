using System.Collections.Generic;

namespace VoxelEngine.Items;

/// <summary>One bonus of a gear set: active once <see cref="Pieces"/> or more of the set are worn.</summary>
public sealed record GearSetBonus(int Pieces, IReadOnlyList<GearModifier> Modifiers);
