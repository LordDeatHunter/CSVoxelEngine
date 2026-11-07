namespace VoxelEngine.WorldGen.Sky;

/// <summary>
/// The biomes of the sky islands (plans/LAYERS.md <c>L-13</c>), chosen by <see cref="SkyBiomes.At"/>.
/// Kept apart from the surface <see cref="Biome"/>, whose ordinals ride the column codecs and whose
/// climate grid the sky has no part in. Never persisted, so the order is free.
/// </summary>
public enum SkyBiome : byte
{
    /// <summary>Bright grass over dirt and stone, flowers and tall grass, broad sky oaks.</summary>
    Meadow,

    /// <summary>Frosted turf over permafrost and frost stone, crystal outcrops, frost ferns, glowing crystal trees.</summary>
    FrostHighland,
}
