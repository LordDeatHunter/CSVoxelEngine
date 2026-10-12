using System;
using System.Numerics;

namespace VoxelEngine.Effects.Post;

/// <summary>
/// GL-free twin of the underwater screen wobble (post-fragment.glsl's <c>uUnderwater</c>): while the
/// eye is under water the whole image sways gently, every pixel read from a point offset by two slow
/// waves across the screen. The post pass feeds that offset point to FXAA, so the anti-aliasing reads
/// around the same displaced coordinate the pixel shows. Zero strength (dry air, lava, the item
/// debugger, which never runs this pass) leaves every pixel where it is.
/// </summary>
public static class UnderwaterWobble
{
    /// <summary>Waves across the screen of the horizontal and the vertical sway.</summary>
    public const float WavesAcross = 2.5f;

    /// <summary>The vertical sway rolls at this share of the horizontal's speed: 83/100, so both
    /// repeat together after exactly 100 horizontal periods (<see cref="Period"/>).</summary>
    public const float VerticalSpeedShare = 0.83f;

    /// <summary>The time after which both sway waves are back where they started, in seconds.</summary>
    public static double Period => 100.0 * Math.Tau / WorldGenerationConfig.UnderwaterWobbleSpeed;

    /// <summary>The lighting clock wrapped into one <see cref="Period"/>, in double precision before
    /// the float the shader gets: the sway never jumps at the wrap and never loses precision.</summary>
    public static float WrapTime(double time)
    {
        var w = time % Period;
        if (w < 0) w += Period;
        return (float)w;
    }

    /// <summary>
    /// The pixel offset at screen position <paramref name="uv"/> (0..1) and lighting
    /// <paramref name="time"/> for a wobble of <paramref name="strength"/> (0..1, the eased underwater
    /// amount) on a screen <paramref name="screenHeight"/> pixels tall: each axis swings by up to
    /// <see cref="WorldGenerationConfig.UnderwaterWobbleScreenShare"/> of the height, the horizontal
    /// with the row and the vertical with the column, at <see cref="WorldGenerationConfig.UnderwaterWobbleSpeed"/>.
    /// Twin of the offset in post-fragment.glsl.
    /// </summary>
    public static Vector2 Offset(Vector2 uv, float time, float strength, float screenHeight)
    {
        var amplitude = MathUtils.Clamp01(strength) * WorldGenerationConfig.UnderwaterWobbleScreenShare * screenHeight;
        var speed = WorldGenerationConfig.UnderwaterWobbleSpeed;
        return new Vector2(
            MathF.Sin(uv.Y * WavesAcross * MathF.Tau + time * speed),
            MathF.Cos(uv.X * WavesAcross * MathF.Tau + time * speed * VerticalSpeedShare)) * amplitude;
    }
}
