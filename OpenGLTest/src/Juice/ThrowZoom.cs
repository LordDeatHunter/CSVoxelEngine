using VoxelEngine.Player;

namespace VoxelEngine.Juice;

/// <summary>
/// GL-free rule of the throw charge zoom: the view narrows a few degrees as a throw winds up and
/// springs back when it is released (or cancelled). <see cref="Camera"/> steps a
/// <see cref="Spring"/> from <see cref="CreateSpring"/> toward <see cref="TargetDegrees"/> through
/// <see cref="Step"/> and adds the result to the base FOV, the sprint widening and the punch pinch,
/// so the terms compose instead of one overwriting another. The spring is under-damped, so the
/// release overshoots a touch wider before it settles: the snap back reads as a spring, not a fade.
/// </summary>
public static class ThrowZoom
{
    /// <summary>
    /// The FOV change the view springs toward this frame, in degrees: 0 with no throw charging, down
    /// to -<see cref="WorldGenerationConfig.ThrowZoomDegrees"/> at a full charge, eased in and out
    /// along the charge so the first and last moments of the wind-up move gently.
    /// <paramref name="charge01"/> is the charge of the throw being wound up, or null when none; a
    /// value outside 0..1 is clamped and a non-finite one reads as no charge.
    /// </summary>
    public static float TargetDegrees(float? charge01)
    {
        if (charge01 is not { } charge || !float.IsFinite(charge))
            return 0f;
        return -WorldGenerationConfig.ThrowZoomDegrees * MathUtils.SmoothStep(charge);
    }

    /// <summary>A fresh zoom spring at rest, tuned by the <c>ThrowZoomSpring*</c> knobs.</summary>
    public static Spring CreateSpring() =>
        new(WorldGenerationConfig.ThrowZoomSpringHz, WorldGenerationConfig.ThrowZoomSpringDamping);

    /// <summary>
    /// Advances <paramref name="spring"/> toward <paramref name="target"/> and returns the zoom to add
    /// to the FOV, clamped so a long frame can never fling the view past a full charge's narrowing,
    /// nor the release overshoot past half of it the wide way.
    /// </summary>
    public static float Step(ref Spring spring, float target, float deltaTime)
    {
        var value = spring.Step(target, deltaTime);
        return MathUtils.Clamp(value, -WorldGenerationConfig.ThrowZoomDegrees,
            WorldGenerationConfig.ThrowZoomDegrees * 0.5f);
    }
}
