namespace VoxelEngine.WorldGen.Sky;

/// <summary>
/// The sky island zone at a point (<see cref="SkyIslands.ZoneAt"/>): how deep the point lies in it
/// (<see cref="Weight"/>, 0 outside every zone, 1 in its core), its size class (<see cref="Tier"/>: 0 small,
/// 1 medium, 2 big; bigger is rarer, and the big zones are where <c>L-15</c>'s hardest sky mobs spawn), its
/// nominal radius and center. The default value is "no zone".
/// </summary>
public readonly record struct SkyZone(float Weight, int Tier, float Radius, float CenterX, float CenterZ)
{
    /// <summary>The tier of the biggest, rarest zones.</summary>
    public const int BigTier = 2;

    /// <summary>Whether the point lies inside a zone at all.</summary>
    public bool InZone => Weight > 0f;
}
