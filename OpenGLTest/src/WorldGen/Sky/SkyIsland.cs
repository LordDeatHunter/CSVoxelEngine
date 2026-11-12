namespace VoxelEngine.WorldGen.Sky;

/// <summary>
/// One accepted floating island (<see cref="SkyIslands.TryGetIsland"/>): its center and nominal radius (the
/// outline only ever wobbles inward of it), the base height its top domes up from and its underside hangs
/// down from, the dome's height at the center and the underside's depth under the center (stalactites aside).
/// </summary>
public readonly record struct SkyIsland(float CenterX, float CenterZ, float Radius, int BaseY, float Dome, float Depth,
    float Aspect = 0f, float Angle = 0f)
{
    /// <summary>Whether this is a great landmass (<see cref="SkyLandmasses"/>) rather than an ordinary round island: its
    /// <see cref="Radius"/> is then the bounding radius, <see cref="Dome"/> the height of its hills and <see cref="Aspect"/>
    /// the short-over-long ratio of its elliptical body, turned by <see cref="Angle"/>.</summary>
    public bool IsLandmass => Aspect > 0f;
}
