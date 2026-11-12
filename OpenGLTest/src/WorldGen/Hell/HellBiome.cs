namespace VoxelEngine.WorldGen.Hell;

/// <summary>
/// The biomes of hell (plans/LAYERS.md L-7), chosen per column and layer by <see cref="HellBiomes.At"/>.
/// Kept apart from the surface <see cref="Biome"/>, whose ordinals ride the column codecs and whose
/// climate grid hell has no part in. Never persisted, so the order is free.
/// </summary>
public enum HellBiome : byte
{
    /// <summary>Gray ash plains over cinder soil, scorched walls, scattered lava pools.</summary>
    Ashlands,

    /// <summary>Basalt caverns whose floors pond lava wherever they dip, studded with magma blocks.</summary>
    MagmaCaverns,

    /// <summary>Fungal moss over fungal soil, lit by fungal glow on the ceilings, nearly dry.</summary>
    FungalGrove,
}
