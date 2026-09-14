namespace silkgltriangle;

/// <summary>
/// The climate of a world column, a pure function of (x, z) (<see cref="TerrainGenerator"/>).
/// Forest keeps the engine's original rolling grass/dirt/stone terrain; Desert builds dunes of
/// sand over sandstone; Ocean is a flooded basin that ramps from a sandy shore down to bare
/// stone. Selection is by two low-frequency noise fields (continentalness and aridity) so the
/// regions are large and contiguous, and the height and surface material blend across their
/// borders rather than stepping.
/// </summary>
public enum Biome : byte
{
    Forest = 0,
    Desert = 1,
    Ocean = 2,
}
