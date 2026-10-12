using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// The post pass's per-frame camera effects (<see cref="PostProcessRenderer.Render"/>), built by
/// <see cref="RenderManager.RenderPostProcess"/>. <see cref="Neutral"/> is the pass with every effect off.
/// </summary>
/// <param name="Exposure">The tonemap exposure: <see cref="WorldGenerationConfig.TonemapExposure"/> times the eye adaptation (<see cref="EyeAdaptation"/>).</param>
/// <param name="Grade">The display-space color grade multiplied over the image (<see cref="BiomeGrade"/>); (1, 1, 1) leaves it alone.</param>
/// <param name="DamageFringe">The damage chromatic fringe's strength (<see cref="silkgltriangle.DamageFringe.Strength"/>); 0 draws none.</param>
/// <param name="Underwater">The underwater wobble's strength, the eased underwater amount (<see cref="UnderwaterWobble"/>); 0 draws none.</param>
/// <param name="Time">The wobble's clock in seconds, wrapped to keep float precision.</param>
public readonly record struct PostEffects(float Exposure, Vector3 Grade, float DamageFringe = 0f, float Underwater = 0f,
    float Time = 0f)
{
    /// <summary>Every effect off: the default exposure and no grade.</summary>
    public static PostEffects Neutral => new(WorldGenerationConfig.TonemapExposure, Vector3.One);
}
