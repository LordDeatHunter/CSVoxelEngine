using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Data;
using VoxelEngine.Items;

namespace VoxelEngine.UI.Hud;

/// <summary>
/// The tooltip one frame shows (GL-free), built by <see cref="ItemTooltip.Hover"/>: <see cref="Anchor"/> is the
/// pointer in window pixels, <see cref="Lines"/> and their <see cref="Colors"/> come from <c>ItemTooltip.Lines</c>,
/// and <see cref="Rarity"/> (<see cref="Rarities.Of"/>) dresses the box. <see cref="BarFraction"/> is the fill of the
/// bar along the box's bottom edge (a lantern's fuel, a crucible's load), null for an item with none.
/// </summary>
public readonly record struct HoverTooltip(
    Vector2 Anchor, IReadOnlyList<string> Lines, IReadOnlyDictionary<int, Rgba> Colors, Rarity Rarity, float? BarFraction = null);
