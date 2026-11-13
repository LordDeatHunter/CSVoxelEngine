namespace VoxelEngine.WorldGen;

/// <summary>
/// The climate of a world column, a pure function of (x, z) (<see cref="TerrainGenerator"/>).
/// Ocean is a flooded basin selected by continentalness; the six land biomes sit on a temperature
/// x humidity grid (<see cref="BiomeClassifier.Classify"/>): Forest keeps the engine's original
/// rolling grass/dirt/stone terrain, Desert builds dunes of sand over sandstone, Taiga is a cold
/// spruce wood under snow, Frozen is a tundra of permafrost under snow over stone, with dead shrubs
/// and snags and occasional ice, and Steppe and Savanna are the dry grasslands between them. Every
/// selection comes from low-frequency noise fields, so regions are large and contiguous, and the
/// height and surface material blend across their borders rather than stepping. The ordinals
/// persist (ColumnFileCodec, the network column) and index per-biome tables, so a new biome is
/// appended, never inserted.
/// </summary>
public enum Biome : byte
{
    Forest = 0,
    Desert = 1,
    Ocean = 2,
    Taiga = 3,
    Frozen = 4,

    /// <summary>Temperate and dry or semi-dry: an open grass plain with a dry tint, sparse trees,
    /// between the forest and both the desert and the tundra on the dry axis.</summary>
    Steppe = 5,

    /// <summary>Warm and semi-dry: a dry grassland with scattered trees, between the forest and the
    /// desert on the warm axis.</summary>
    Savanna = 6,

    /// <summary>The hottest, wettest corner of the climate: a lowland rainforest of very tall trees
    /// and heavy undergrowth over mud and shallow pools. Carved out of the warm, wet forest, so it
    /// borders forest, savanna and ocean, never desert or snow.</summary>
    Jungle = 7,
}
