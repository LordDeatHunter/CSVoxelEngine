using System;
using System.Numerics;

namespace VoxelEngine.Effects.Sky;

/// <summary>
/// How the realm layers (plans/LAYERS.md) color the sky and the fog around the eye, GL-free: under
/// the barrier band, in hell, the sky palette (which the dome, the world fog and the clear color all
/// sample) turns to <see cref="SkyConfig.HellFogColor"/> and the fog closes in; above the solid cloud
/// slab it shifts toward a slightly deeper, brighter blue, fully so at the sky islands. Both amounts
/// are targets of the eye's height, ramped so crossing a boundary never pops, and eased in time like
/// the underwater and lava eye states (<see cref="Ease"/>). <c>FrameLighting</c> applies them
/// once a frame to the sRGB palette before it is decoded, so no shader changes: every
/// <c>SkySample</c> copy reads the shifted palette through its usual uniforms.
/// </summary>
public static class LayerClimate
{
    // Where hell begins in world space: the floor of the barrier band's lowest cell
    private const float HellTop = WorldGenerationConfig.HellTopY + 1f;

    /// <summary>The hell tint, decoded once to linear light, which is what the fog reads.</summary>
    public static readonly Vector3 HellFogColorLinear = ColorSpace.SrgbToLinear(SkyConfig.HellFogColor);

    /// <summary>
    /// Which hell layer the height <paramref name="y"/> lies in: -1 above hell, 0 for the first layer
    /// under the barrier, one more every <see cref="WorldGenerationConfig.HellLayerPeriodBlocks"/>
    /// downward, with no bottom.
    /// </summary>
    public static int HellLayerIndex(float y) =>
        y >= HellTop ? -1 : (int)MathF.Floor((HellTop - y) / WorldGenerationConfig.HellLayerPeriodBlocks);

    /// <summary>
    /// The hell tint the eye at <paramref name="eyeY"/> should see, 0..1: exactly 0 at and above
    /// hell's top (the barrier band, the caves, the surface), rising smoothly to 1 over
    /// <see cref="SkyConfig.HellFogRampBlocks"/> below it and staying 1 all the way down.
    /// </summary>
    public static float HellTarget(float eyeY) =>
        MathUtils.SmoothStep(MathUtils.Clamp01((HellTop - eyeY) / SkyConfig.HellFogRampBlocks));

    /// <summary>
    /// The altitude shift the eye at <paramref name="eyeY"/> should see, 0..1: exactly 0 at and below
    /// the cloud slab's top, rising smoothly to 1 at the sky islands' bottom and staying 1 above.
    /// </summary>
    public static float AltitudeTarget(float eyeY) =>
        MathUtils.SmoothStep(MathUtils.Clamp01((eyeY - WorldGenerationConfig.CloudSlabTopY)
                                               / (WorldGenerationConfig.SkyIslandBottomY - WorldGenerationConfig.CloudSlabTopY)));

    /// <summary>Eases an amount toward its <paramref name="target"/> over <paramref name="dt"/>
    /// seconds (<see cref="SkyConfig.LayerFogEaseRate"/>), never past it.</summary>
    public static float Ease(float current, float target, float dt) =>
        MathUtils.Damp(current, target, SkyConfig.LayerFogEaseRate, dt);

    /// <summary>
    /// The sRGB <paramref name="palette"/> with the altitude shift (<paramref name="altitude"/>, a
    /// per-channel multiply by <see cref="SkyConfig.SkyAltitudeTint"/>, so night stays dark) and then
    /// the hell tint (<paramref name="hell"/>: zenith and horizon toward
    /// <see cref="SkyConfig.HellFogColor"/>, the sunset glow out). Both 0 leaves it unchanged.
    /// </summary>
    public static SkyPalette Apply(SkyPalette palette, float altitude, float hell)
    {
        var tint = Vector3.Lerp(Vector3.One, SkyConfig.SkyAltitudeTint, MathUtils.Clamp01(altitude));
        var zenith = Vector3.Min(palette.Zenith * tint, Vector3.One);
        var horizon = Vector3.Min(palette.Horizon * tint, Vector3.One);
        hell = MathUtils.Clamp01(hell);
        return new SkyPalette(
            Vector3.Lerp(zenith, SkyConfig.HellFogColor, hell),
            Vector3.Lerp(horizon, SkyConfig.HellFogColor, hell),
            palette.Glow * (1f - hell));
    }
}
