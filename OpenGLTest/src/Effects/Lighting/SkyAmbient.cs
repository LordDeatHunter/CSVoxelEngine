using System;
using System.Numerics;

namespace VoxelEngine.Effects.Lighting;

/// <summary>
/// GL-free twin of the shader's <c>SkyGate</c> and the hemispheric ambient term of
/// <c>test-fragment.glsl</c>: ambient is skylight bounce, so it reaches a fragment exactly as far as
/// the sky does (<c>fSun</c>). With no skylight there is no ambient at all — a sealed room is lit by
/// torch, point and emissive light only, and never changes with the time of day.
/// </summary>
public static class SkyAmbient
{
    /// <summary>The share of the ambient a fragment with this skylight receives: <paramref name="sun"/> itself.</summary>
    public static float Gate(float sun) => Math.Clamp(sun, 0f, 1f);

    /// <summary>The lit color: gated hemispheric ambient plus the stronger of the direct sun and local light.</summary>
    public static Vector3 Compose(Vector3 ambient, Vector3 groundAmbient, float normalY, float sun,
        Vector3 directSun, Vector3 local)
    {
        var hemi = Vector3.Lerp(groundAmbient, ambient, 0.5f + 0.5f * normalY);
        return hemi * Gate(sun) + Vector3.Max(directSun, local);
    }
}
