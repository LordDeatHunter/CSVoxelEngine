using System;
using System.Numerics;

namespace VoxelEngine.Rendering;

/// <summary>
/// The GL-free decision behind the reflection pass's temporal reuse: given the camera state that
/// produced the cached planar mirror and the state of the current frame, decide whether the mirror
/// must be re-rendered or whether the existing texture (sampled with the view-projection that
/// produced it) is still good enough.
///
/// The class is deliberately dependency-free — <c>Vector3</c> and <c>MathF</c> only, no GL, no
/// <c>Camera</c>, no <c>RenderManager</c> — because <c>RenderManager</c> needs a live GL context to
/// construct, so the only way to pin this policy headlessly is to keep it out here. Thresholds live
/// in <c>WorldGenerationConfig</c> (ReflectionReuseMax*) so they can be retuned in one place.
/// </summary>
internal static class ReflectionReuse
{
    /// <summary>The camera state that produced a mirror (or that the current frame would draw with).</summary>
    internal readonly record struct ReflectionPose(
        Vector3 Eye,
        Vector3 Forward,
        float FovDegrees,
        float Aspect,
        float PlaneY);

    // Comparing dot products avoids an acos per frame; the threshold is a constant, so is the cosine.
    private static readonly float CosMaxAngle =
        MathUtils.CosDeg(WorldGenerationConfig.ReflectionReuseMaxDegrees);

    /// <summary>
    /// True when the mirror has to be re-rendered this frame; false when the cached texture and its
    /// view-projection may be reused as-is.
    /// </summary>
    /// <param name="cached">Camera state that produced the cached mirror.</param>
    /// <param name="current">Camera state of the frame being drawn.</param>
    /// <param name="hasCachedMirror">A real mirror render happened and its texture still matches <paramref name="cached"/>.</param>
    /// <param name="targetRecreated">The reflection render target was (re)created this frame, so its contents are undefined.</param>
    /// <param name="reusedLastFrame">The previous frame already skipped the mirror render.</param>
    /// <param name="entitiesMoved">An entity drawn in the mirror moved since the cached render
    /// (<see cref="EntitiesMoved"/>), so the cached texture shows it in a stale place.</param>
    internal static bool ShouldRefresh(
        in ReflectionPose cached,
        in ReflectionPose current,
        bool hasCachedMirror,
        bool targetRecreated,
        bool reusedLastFrame,
        bool entitiesMoved = false)
    {
        // Nothing to reuse, the texture is undefined, or reusing again would let the mirror (and the
        // shadow map, sun and clouds baked into it) step at fps/2 for more than a single frame.
        if (!hasCachedMirror || targetRecreated || reusedLastFrame)
            return true;

        // A mirrored entity moved: however still the camera is, its reflection would lag it.
        if (entitiesMoved)
            return true;

        // The mirror plane itself moved, so every mirrored vertex is wrong.
        if (current.PlaneY != cached.PlaneY)
            return true;

        // Projection change (sprint FOV ease) — it is baked into the cached view-projection.
        if (MathF.Abs(current.FovDegrees - cached.FovDegrees) > WorldGenerationConfig.ReflectionReuseMaxFovDegrees)
            return true;

        // The other projection input: a resize can change the aspect without changing the texture size.
        if (MathF.Abs(current.Aspect - cached.Aspect) > WorldGenerationConfig.ReflectionReuseMaxAspect)
            return true;

        // Eye moved far enough that the parallax of the one-frame-old mirror would read as swimming.
        if (Vector3.DistanceSquared(current.Eye, cached.Eye) >
            WorldGenerationConfig.ReflectionReuseMaxMoveBlocks * WorldGenerationConfig.ReflectionReuseMaxMoveBlocks)
            return true;

        // Degenerate input: a zero forward has no angle to compare, so refuse to reuse.
        if (cached.Forward.LengthSquared() < 1e-12f || current.Forward.LengthSquared() < 1e-12f)
            return true;

        // Angular delta: the mirror only covers the frustum of the frame that drew it, and the shader
        // clamps lookups outside it, so a turn past the threshold smears the texture's screen edge.
        return Vector3.Dot(Vector3.Normalize(cached.Forward), Vector3.Normalize(current.Forward)) < CosMaxAngle;
    }

    /// <summary>
    /// True when the mirrored entities are not where the cached mirror drew them: the counts
    /// differ, or any index-matched pair is farther apart than <paramref name="maxMoveBlocks"/>.
    /// Both lists must be built in the same stable order (entity kind, then list order), or an
    /// unchanged scene would compare mismatched pairs.
    /// </summary>
    internal static bool EntitiesMoved(ReadOnlySpan<Vector3> cached, ReadOnlySpan<Vector3> current, float maxMoveBlocks)
    {
        if (cached.Length != current.Length)
            return true;

        var max2 = maxMoveBlocks * maxMoveBlocks;
        for (var i = 0; i < current.Length; i++)
        {
            if (Vector3.DistanceSquared(cached[i], current[i]) > max2)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The eye-side gate in front of the mirror: a planar reflection across the sea plane is only
    /// meaningful with the eye above that plane AND in air. A submerged eye that is still above the
    /// plane (a pond standing above sea level) would otherwise get a mirror built across a plane far
    /// below the pond's own surface, which the water projected into a smeared streak; from under the
    /// surface the shader shows the Snell window instead and never samples the mirror.
    /// </summary>
    internal static bool MirrorApplies(float eyeY, float planeY, bool eyeInWater)
        => !eyeInWater && eyeY >= planeY;
}
