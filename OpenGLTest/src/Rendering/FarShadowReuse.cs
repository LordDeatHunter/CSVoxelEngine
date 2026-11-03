using System;
using System.Numerics;

namespace VoxelEngine.Rendering;

/// <summary>
/// The GL-free decision behind the far shadow cascade's reuse: given the pose the cached far map was
/// rendered from and the state of the current frame, decide whether the map must be re-rendered or
/// whether the existing depth texture (sampled with the view-projection that produced it) still holds.
///
/// The class is deliberately dependency-free (<c>Vector3</c> and <c>MathF</c> only, no GL, no
/// <c>Camera</c>, no <c>ShadowPass</c>), because the shadow pass needs a live GL context to construct,
/// so the only way to pin this policy headlessly is to keep it out here. Thresholds live in
/// <c>LightingConfig</c> (FarShadowReuse*) so they can be retuned in one place.
/// </summary>
internal static class FarShadowReuse
{
    /// <summary>Why the far map is re-rendered this frame, or <see cref="None"/> to reuse it.</summary>
    internal enum Refresh
    {
        None,
        NoMap,
        Handover,
        KeyLightTurn,
        Recenter,
        Depth,
        MaxAge,
        Geometry,
    }

    /// <summary>What the cached map was rendered from: the box center, the eye, the key light, which
    /// body the key light was (the moon or the sun) and the caster geometry version.</summary>
    internal readonly record struct Pose(
        Vector3 Center,
        Vector3 Eye,
        Vector3 KeyLight,
        bool MoonLit,
        int GeometryVersion);

    /// <summary>The current frame: the look-following point the box wants to center on, the eye, the
    /// key light, which body it is and the caster geometry version.</summary>
    internal readonly record struct Frame(
        Vector3 Target,
        Vector3 Eye,
        Vector3 KeyLight,
        bool MoonLit,
        int GeometryVersion);

    /// <summary>
    /// The first reason the far map has to be re-rendered this frame, or <see cref="Refresh.None"/>
    /// when the cached map and its view-projection may be reused as-is.
    /// </summary>
    /// <param name="cached">The pose the cached map was rendered from.</param>
    /// <param name="current">The frame being drawn.</param>
    /// <param name="hasMap">A render happened and was confirmed, so the map holds <paramref name="cached"/>.</param>
    /// <param name="framesSinceRender">Frames since that render, counting the current one (1 on the
    /// frame after a render).</param>
    /// <param name="radiusBlocks">The far box's half-extent.</param>
    /// <param name="mapSize">The far map's resolution in texels.</param>
    internal static Refresh Decide(in Pose cached, in Frame current, bool hasMap, int framesSinceRender,
        float radiusBlocks, int mapSize)
    {
        if (!hasMap)
            return Refresh.NoMap;

        // Moonlight.Direction negates the light at the handover. The cross product of two
        // anti-parallel lights is zero, so the turn test below would only catch it through its
        // Dot guard; the explicit flag names the cause.
        if (cached.MoonLit != current.MoonLit)
            return Refresh.Handover;

        if (KeyLightTurned(cached.KeyLight, current.KeyLight, radiusBlocks, mapSize))
            return Refresh.KeyLightTurn;

        // The hysteresis re-center: the held center stays until the look-following target leaves a
        // square around it in the cached light's lateral axes. Motion along the light is depth, below.
        var (right, up) = ShadowCascade.LightAxes(cached.KeyLight);
        var d = current.Target - cached.Center;
        var lateral = MathF.Max(MathF.Abs(Vector3.Dot(d, right)), MathF.Abs(Vector3.Dot(d, up)));
        if (lateral > LightingConfig.FarShadowReuseInnerFraction * radiusBlocks)
            return Refresh.Recenter;

        var depth = Vector3.Dot(current.Eye - cached.Eye, Vector3.Normalize(cached.KeyLight));
        if (MathF.Abs(depth) > LightingConfig.FarShadowReuseDepthMarginFraction * radiusBlocks)
            return Refresh.Depth;

        if (framesSinceRender >= LightingConfig.FarShadowReuseMaxFrames)
            return Refresh.MaxAge;

        if (current.GeometryVersion != cached.GeometryVersion
            && framesSinceRender >= LightingConfig.FarShadowReuseGeometryMinFrames)
            return Refresh.Geometry;

        return Refresh.None;
    }

    /// <summary>True when the key light turned past <c>FarShadowReuseMaxTurnTexels</c> of a far texel
    /// at the box edge, or either light is degenerate or the two face away from each other.</summary>
    private static bool KeyLightTurned(Vector3 cached, Vector3 current, float radiusBlocks, int mapSize)
    {
        if (cached.LengthSquared() < 1e-12f || current.LengthSquared() < 1e-12f)
            return true;
        if (Vector3.Dot(cached, current) <= 0f)
            return true;

        // A turn of t radians moves the box edge (a radius out) by about r * t, so the bound in
        // radians is the allowed texels times the texel size over the radius. Compared as a sine:
        // the cosine of a ~1/2048 rad turn is below float resolution.
        var maxTurn = LightingConfig.FarShadowReuseMaxTurnTexels * (2f * radiusBlocks / mapSize) / radiusBlocks;
        return Vector3.Cross(Vector3.Normalize(cached), Vector3.Normalize(current)).Length() > maxTurn;
    }
}
