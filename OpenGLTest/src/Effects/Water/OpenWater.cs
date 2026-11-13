using System;
using VoxelEngine.Chunks;

namespace VoxelEngine.Effects.Water;

/// <summary>
/// How much the water around a submerged eye is open water (a sea, a wide lake) rather than a pond,
/// a river or a flooded cave: the share of probe columns around the eye that carry water at the
/// eye's own surface level. Open water gets the ocean look (a longer, bluer fog that brightens
/// toward the surface and darkens toward the deep, test-fragment.glsl's <c>WetFogColor</c>); a
/// small body keeps the close, plain underwater fog.
/// </summary>
public static class OpenWater
{
    // Unit directions of the probe rings: the 8 compass points
    private static readonly (float X, float Z)[] Directions =
    [
        (1f, 0f), (0.7071f, 0.7071f), (0f, 1f), (-0.7071f, 0.7071f),
        (-1f, 0f), (-0.7071f, -0.7071f), (0f, -1f), (0.7071f, -0.7071f),
    ];

    /// <summary>
    /// The share (0..1) of the probe columns around (<paramref name="eyeX"/>, <paramref name="eyeZ"/>),
    /// 8 directions at each of <see cref="WaterRenderConfig.OpenWaterProbeRadii"/>, whose cell just
    /// under <paramref name="surfaceY"/> is fluid. Main thread (it reads loaded cells); an unloaded
    /// column counts as dry.
    /// </summary>
    internal static float Share(ChunkManager chunks, int eyeX, int eyeZ, float surfaceY)
    {
        var y = (int)MathF.Floor(surfaceY - 0.5f);
        var radii = WaterRenderConfig.OpenWaterProbeRadii;
        var wet = 0;
        foreach (var radius in radii)
        {
            foreach (var (dx, dz) in Directions)
            {
                var x = eyeX + (int)MathF.Round(dx * radius);
                var z = eyeZ + (int)MathF.Round(dz * radius);
                if (float.IsFinite(chunks.GetFluidSurfaceGlobal(x, y, z)))
                    wet++;
            }
        }

        return wet / (float)(radii.Length * Directions.Length);
    }

    /// <summary>
    /// The ocean look's weight (0..1) for a probe <paramref name="share"/>: zero up to
    /// <see cref="WaterRenderConfig.OpenWaterShareNear"/> (a pond, a river, a cave pool), one from
    /// <see cref="WaterRenderConfig.OpenWaterShareFar"/> (water most of the way around), never
    /// decreasing in between.
    /// </summary>
    public static float Amount(float share) =>
        MathUtils.SmoothStep(WaterRenderConfig.OpenWaterShareNear, WaterRenderConfig.OpenWaterShareFar, share);

    /// <summary>
    /// What the open-water fog color is multiplied by along a view direction whose vertical
    /// component is <paramref name="dirY"/> (-1 straight down, 1 straight up), at ocean weight
    /// <paramref name="openWater"/>: darker toward the deep, brighter toward the lit surface, and
    /// exactly 1 (the plain fog) in a small body. Twin of test-fragment.glsl's <c>WetFogColor</c>.
    /// </summary>
    public static float FogShade(float dirY, float openWater)
    {
        var shade = MathUtils.Lerp(WaterRenderConfig.OceanFogShadeDown, WaterRenderConfig.OceanFogShadeUp,
            MathUtils.SmoothStep(-WaterRenderConfig.OceanFogShadeSpan, WaterRenderConfig.OceanFogShadeSpan, dirY));
        return MathUtils.Lerp(1f, shade, MathUtils.Clamp01(openWater));
    }
}
