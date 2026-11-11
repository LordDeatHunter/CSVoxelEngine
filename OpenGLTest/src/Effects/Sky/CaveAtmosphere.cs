using System.Numerics;

namespace VoxelEngine.Effects.Sky;

/// <summary>
/// The cave biome atmosphere around the eye (<see cref="CaveClimate"/>, plans/CAVE_BIOMES.md CB-3):
/// <see cref="Strength"/> in 0..1 and four values stored at FULL strength, the sRGB fog color the sky
/// palette turns to, the fog's near and far distances in blocks and the per-channel ambient factor.
/// Every effect lerps from neutral toward its full value by <see cref="Strength"/>, so an atmosphere
/// of strength 0 changes nothing whatever its other fields hold.
/// </summary>
public readonly record struct CaveAtmosphere(float Strength, Vector3 FogColorSrgb, float FogNearBlocks, float FogFarBlocks,
    Vector3 AmbientTint)
{
    /// <summary>No atmosphere: strength 0 and every full value neutral (the plain caves, the surface).</summary>
    public static readonly CaveAtmosphere None =
        new(0f, Vector3.Zero, LightingConfig.FogNearBlocks, LightingConfig.FogFarBlocks, Vector3.One);
}
