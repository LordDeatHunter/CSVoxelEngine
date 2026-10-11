using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// GL-free twin of the aurora's fade: slow green and violet curtains in the night dome
/// (<c>AuroraCurtains</c> in sky-fragment.glsl), seen only from cold biomes. Their shape lives in the
/// shader; how strongly they show this frame is decided here, from how cold it is at the eye (the
/// climate there, eased so walking across a biome border fades them rather than switching them) and
/// how visible the stars are (<see cref="StarField.Visibility"/>, so they are gone by day and fade in
/// over dusk with the stars).
/// </summary>
public static class Aurora
{
    /// <summary>
    /// Cells round the horizon of the shader's four periodic noises (the broad curtain sectors, their
    /// detail, the lower edge's wave and the fine folds), and how fast each drifts relative to
    /// <see cref="WorldGenerationConfig.AuroraDriftSpeed"/>. Twin of the periods in sky-fragment.glsl.
    /// </summary>
    public static readonly Vector4 Periods = new(7f, 17f, 11f, 160f);

    private static readonly Vector4 DriftRates = new(1f, -1.3f, 0.7f, 6f);

    /// <summary>
    /// Each noise's drift at lighting <paramref name="time"/>, in cells, wrapped into 0..its period
    /// (<see cref="Periods"/>) on the CPU in double precision: a noise that repeats every period cells
    /// looks the same after the wrap, so the curtains drift on forever with no jump and the shader
    /// never sees a large float.
    /// </summary>
    public static Vector4 Drift(double time)
    {
        var t = time * WorldGenerationConfig.AuroraDriftSpeed;
        return new Vector4(Wrap(t * DriftRates.X, Periods.X), Wrap(t * DriftRates.Y, Periods.Y),
            Wrap(t * DriftRates.Z, Periods.Z), Wrap(t * DriftRates.W, Periods.W));
    }

    private static float Wrap(double cells, float period)
    {
        var w = cells % period;
        if (w < 0) w += period;
        return (float)w;
    }

    /// <summary>
    /// How strongly the curtains show, 0..1, for an eased <paramref name="coldness"/> at the eye
    /// (0..1, <see cref="EyeClimate.Coldness"/>) and the stars' <paramref name="starVisibility"/>:
    /// zero at or below <see cref="WorldGenerationConfig.AuroraColdStart"/> (the temperate and warm
    /// lands), full from <see cref="WorldGenerationConfig.AuroraColdFull"/> on, times the stars'.
    /// </summary>
    public static float Visibility(float coldness, float starVisibility) =>
        MathUtils.SmoothStep(WorldGenerationConfig.AuroraColdStart, WorldGenerationConfig.AuroraColdFull, coldness)
        * MathUtils.Clamp01(starVisibility);

    /// <summary>
    /// The coldness the eased value heads for: the climate's when the eye's column is known
    /// (<paramref name="hasClimate"/>), else 0, so an unknown column (a client before its column
    /// streams in, no world at all) fades the aurora out rather than guessing.
    /// </summary>
    public static float TargetColdness(bool hasClimate, float warmth) =>
        hasClimate ? EyeClimate.Coldness(warmth) : 0f;

    /// <summary>Eases <paramref name="current"/> toward <paramref name="target"/> over
    /// <paramref name="deltaTime"/> seconds at <see cref="WorldGenerationConfig.AuroraColdEaseRate"/>.</summary>
    public static float Ease(float current, float target, float deltaTime) =>
        MathUtils.Damp(current, target, WorldGenerationConfig.AuroraColdEaseRate, deltaTime);
}
