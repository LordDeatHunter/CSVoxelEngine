namespace VoxelEngine.Effects.Water;

/// <summary>
/// How a submerged eye's view ray splits between water and air. The underwater fog is the water
/// between the eye and what it looks at, so only the wet part of a ray is charged for it: terrain
/// and clouds above the surface are seen through the few blocks of water under the surface, not
/// through their whole distance, which is what keeps the world above readable from below. Twin of
/// test-fragment.glsl's <c>WetShare</c>.
/// </summary>
public static class UnderwaterPath
{
    /// <summary>
    /// The share (0..1) of the straight ray from an eye at <paramref name="eyeY"/> to a point at
    /// <paramref name="pointY"/> that runs below the water surface at <paramref name="surfaceY"/>:
    /// all of it for a point at or below the surface, and less the higher the point stands above
    /// it. Meant for a submerged eye: one above the surface looking up has no wet part.
    /// </summary>
    public static float WetShare(float eyeY, float surfaceY, float pointY)
    {
        if (!(pointY > surfaceY)) return 1f;
        return MathUtils.Clamp01((surfaceY - eyeY) / (pointY - eyeY));
    }
}
