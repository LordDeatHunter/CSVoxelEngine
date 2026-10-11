using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// The post pass's per-frame camera effects (<see cref="PostProcessRenderer.Render"/>), built by
/// <see cref="RenderManager.RenderPostProcess"/>. <see cref="Neutral"/> is the pass with every effect off.
/// </summary>
/// <param name="Exposure">The tonemap exposure: <see cref="WorldGenerationConfig.TonemapExposure"/> times the eye adaptation (<see cref="EyeAdaptation"/>).</param>
/// <param name="Grade">The display-space colour grade multiplied over the image (<see cref="BiomeGrade"/>); (1, 1, 1) leaves it alone.</param>
/// <param name="DamageFringe">The damage chromatic fringe's strength (<see cref="silkgltriangle.DamageFringe.Strength"/>); 0 draws none.</param>
public readonly record struct PostEffects(float Exposure, Vector3 Grade, float DamageFringe = 0f)
{
    /// <summary>Every effect off: the default exposure and no grade.</summary>
    public static PostEffects Neutral => new(WorldGenerationConfig.TonemapExposure, Vector3.One);
}
