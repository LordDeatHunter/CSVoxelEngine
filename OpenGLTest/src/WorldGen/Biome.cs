namespace VoxelEngine.WorldGen;

/// <summary>
/// The climate of a world column, a pure function of (x, z) (<see cref="TerrainGenerator"/>).
/// Ocean is a flooded basin selected by continentalness; the four land biomes sit on a temperature
/// x humidity grid: Forest keeps the engine's original rolling grass/dirt/stone terrain, Desert
/// builds dunes of sand over sandstone, Taiga is a cold spruce wood under snow, and Frozen is a
/// barren snow plain over rock with occasional ice. Every selection comes from low-frequency noise
/// fields, so regions are large and contiguous, and the height and surface material blend across
/// their borders rather than stepping.
/// </summary>
public enum Biome : byte
{
    Forest = 0,
    Desert = 1,
    Ocean = 2,
    Taiga = 3,
    Frozen = 4,
}
