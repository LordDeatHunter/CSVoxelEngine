namespace VoxelEngine.WorldGen.Caves;

/// <summary>
/// The cave biome an overworld rock cell belongs to (<see cref="CaveBiomes.At"/>, plans/CAVE_BIOMES.md).
/// Its own enum, never the surface <see cref="Biome"/>, whose ordinals ride the column codecs; append new
/// values only, never insert.
/// </summary>
public enum CaveBiome
{
    /// <summary>Unchanged stone: the caves of every surface biome without an echo, and the gaps between
    /// the sub-biomes. The palette pass writes nothing here.</summary>
    Plain,

    /// <summary>A frozen column's caves, echoing its surface: frost stone and permafrost floors.</summary>
    FrozenEcho,

    /// <summary>A desert column's caves, echoing its surface: sandstone.</summary>
    DesertEcho,

    /// <summary>A sub-band region whose cave faces are studded with crystal.</summary>
    Crystal,

    /// <summary>A sub-band region of fungal moss floors, fungal soil ceilings and glowing fungus.</summary>
    Fungal,

    /// <summary>The magma-touched deep right above the barrier: scorched rock and glowing magma.</summary>
    MagmaDeep,
}
