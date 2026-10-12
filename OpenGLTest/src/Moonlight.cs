using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// GL-free key-light rule: what lights the world directly and casts its shadows. By day that is the
/// sun; once the sun is well below the horizon it is the moon, a faint cool light from the moon's
/// direction (the sun's negated, see <see cref="MoonPhase"/>), so a night keeps soft shadows and a
/// blue cast. The handover happens at <see cref="WorldGenerationConfig.MoonlightSwitchSunElevation"/>
/// with both lights at zero strength: the sun's light eases out on its way down to it
/// (<see cref="SunFade"/>) and the moon's eases in from it as the moon climbs (<see cref="Rise"/>), so
/// neither the light nor the shadow direction ever pops. The switch also lies at or below the end of
/// the sunset glow (<see cref="SkyPalette.GlowAmount"/>), because the world and cloud shaders' sky
/// sample reads the key light's direction: with no glow, that direction changes nothing there.
/// </summary>
public static class Moonlight
{
    /// <summary>The moonlight's cool hue, authored in sRGB like every palette color.</summary>
    public static readonly Vector3 ColorSrgb = new(0.55f, 0.66f, 0.95f);

    /// <summary><see cref="ColorSrgb"/> decoded, for the linear world shader.</summary>
    public static readonly Vector3 ColorLinear = ColorSpace.SrgbToLinear(ColorSrgb);

    /// <summary>Whether a sun at sine-elevation <paramref name="sunElevation"/> has handed the world's
    /// light to the moon (at and below the switch).</summary>
    public static bool IsMoonLit(float sunElevation) => sunElevation <= WorldGenerationConfig.MoonlightSwitchSunElevation;

    /// <summary>The unit direction the world is lit from: <paramref name="sunDir"/> by day, the moon's
    /// (<paramref name="sunDir"/> negated) once <see cref="IsMoonLit"/>.</summary>
    public static Vector3 Direction(Vector3 sunDir) => IsMoonLit(sunDir.Y) ? -sunDir : sunDir;

    /// <summary>The share of its own light the sun keeps as it sets: 1 while it is up, easing to 0 at
    /// the switch, so the floor it lit the night with from below the horizon is gone by the handover.</summary>
    public static float SunFade(float sunElevation) =>
        MathUtils.SmoothStep(WorldGenerationConfig.MoonlightSwitchSunElevation, 0f, sunElevation);

    /// <summary>How far the moonlight has come in, 0 at the switch to 1 once the sun is
    /// <see cref="WorldGenerationConfig.MoonlightRiseBand"/> further down (the moon that much higher).</summary>
    public static float Rise(float sunElevation) =>
        MathUtils.SmoothStep(WorldGenerationConfig.MoonlightSwitchSunElevation,
            WorldGenerationConfig.MoonlightSwitchSunElevation - WorldGenerationConfig.MoonlightRiseBand, sunElevation);

    /// <summary>How much of the full moon's light phase <paramref name="phase"/> gives
    /// (<see cref="MoonPhase.Fraction"/>): <see cref="WorldGenerationConfig.MoonlightNewMoonShare"/> at a
    /// new moon, rising with the lit share of the disc to 1 at full.</summary>
    public static float PhaseShare(float phase) =>
        MathUtils.Lerp(WorldGenerationConfig.MoonlightNewMoonShare, 1f, MoonPhase.IlluminatedFraction(phase));

    /// <summary>The moonlight's strength in the world shader's sun-intensity units for a sun at
    /// sine-elevation <paramref name="sunElevation"/> and moon phase <paramref name="phase"/>; zero
    /// while the sun still lights the world.</summary>
    public static float Intensity(float sunElevation, float phase) => IsMoonLit(sunElevation)
        ? WorldGenerationConfig.MoonlightIntensity * Rise(sunElevation) * PhaseShare(phase)
        : 0f;
}
