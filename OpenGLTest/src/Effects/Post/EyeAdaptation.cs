using System;

namespace silkgltriangle;

/// <summary>
/// GL-free eye adaptation: the tonemap exposure eases up in a dark cave and back down outside. The
/// light it adapts to is the voxel light at the eye's cell (<see cref="EntityLight.Sample"/>: the
/// sky light, 0 deep underground, and the block light of lamps, torches and lava), never a GPU
/// readback, so it is a cheap CPU number a frame. Open sky, whatever the hour, and any well-lit room
/// keep the exposure at exactly 1, so the day, the night and a lamp-lit room look as they always
/// did; only darkness raises it, to at most <see cref="WorldGenerationConfig.EyeAdaptMaxExposure"/>.
/// The eye adapts to the dark slowly and back to the light quickly.
/// </summary>
public static class EyeAdaptation
{
    /// <summary>
    /// The exposure multiplier the eye heads for under sky light <paramref name="sun"/> and block light
    /// <paramref name="block"/> (both 0..1, <see cref="EntityLightSample"/>): 1 once the brighter of
    /// the two reaches <see cref="WorldGenerationConfig.EyeAdaptFullLight"/>, rising smoothly to
    /// <see cref="WorldGenerationConfig.EyeAdaptMaxExposure"/> in total darkness.
    /// </summary>
    public static float Target(float sun, float block)
    {
        var light = MathUtils.Clamp01(MathF.Max(sun, block));
        return MathUtils.Lerp(WorldGenerationConfig.EyeAdaptMaxExposure, 1f,
            MathUtils.SmoothStep(0f, WorldGenerationConfig.EyeAdaptFullLight, light));
    }

    /// <summary>
    /// Eases <paramref name="current"/> toward <paramref name="target"/> over <paramref name="deltaTime"/>
    /// seconds: at <see cref="WorldGenerationConfig.EyeAdaptToDarkRate"/> while the exposure rises (the
    /// eye getting used to the dark) and the faster <see cref="WorldGenerationConfig.EyeAdaptToLightRate"/>
    /// while it falls (stepping back out into the light).
    /// </summary>
    public static float Step(float current, float target, float deltaTime) =>
        MathUtils.Damp(current, target,
            target > current ? WorldGenerationConfig.EyeAdaptToDarkRate : WorldGenerationConfig.EyeAdaptToLightRate,
            deltaTime);
}
