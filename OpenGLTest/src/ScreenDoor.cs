using System;

namespace silkgltriangle;

/// <summary>
/// GL-free twin of the screen-door stream-in fade in test-fragment.glsl (SH-25, the <c>Ign</c>
/// helper and the <c>CHUNK_DRAW</c> discard): an opaque chunk or LOD land fragment that is still
/// fading in survives only where its fade beats the pixel's interleaved-gradient-noise threshold,
/// so the fade is coverage rather than blending and the survivors stay opaque with honest depth.
/// Keep the two in step.
/// </summary>
public static class ScreenDoor
{
    /// <summary>
    /// Interleaved gradient noise (Jimenez 2014) at a pixel centre, i.e. <c>gl_FragCoord.xy</c>
    /// (integer + 0.5): a threshold in [0, 1) that is evenly spread over any small block of pixels.
    /// </summary>
    public static float Threshold(float px, float py) =>
        Frac(52.9829189f * Frac(px * 0.06711056f + py * 0.00583715f));

    /// <summary>
    /// Whether a fragment with fade <paramref name="alpha"/> survives the screen door: a fully
    /// faded-in fragment always does, otherwise only where the fade beats the threshold.
    /// </summary>
    public static bool Keeps(float alpha, float px, float py) => alpha >= 1f || alpha > Threshold(px, py);

    private static float Frac(float x) => x - MathF.Floor(x);
}
