namespace VoxelEngine.Items;

/// <summary>One stat change of a piece of gear: a stat and how much it moves (whole points for the flat stats,
/// a fraction for the rest).</summary>
public readonly record struct GearModifier(GearStat Stat, float Value);
