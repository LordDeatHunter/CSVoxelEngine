using Pose = VoxelEngine.Player.PlayerModel.Pose;

namespace VoxelEngine.Player.Posing;

/// <summary>
/// The staff cast's gesture as pure functions of its progress t (0..1, <see cref="PlayerModel.CastProgress"/>):
/// the right arm raises the staff up over the head-front, thrusts it straight forward with a snap, recoils a
/// little and eases back to whatever the base pose holds, with a small lean back on the raise and forward into
/// the thrust. Cosmetic only; the held item stays the staff.
/// </summary>
internal static class CastMotion
{
    // Phase ends as shares of the cast: the raise, the snapping thrust, the recoil, then the eased return
    internal const float RaiseEnd = 0.28f;
    internal const float ThrustEnd = 0.48f;
    internal const float RecoilEnd = 0.62f;

    // Absolute joint targets in radians, the ThrowPose convention (positive shoulder = forward flex)
    internal const float RaisedShoulder = 2.3f;
    internal const float RaisedElbow = 1.0f;
    internal const float ThrustShoulder = 1.55f;
    internal const float ThrustElbow = 0.05f;
    internal const float RecoilShoulder = 1.35f;
    internal const float RecoilElbow = 0.35f;

    // Torso pitch the gesture adds (negative = forward): back a touch on the raise, forward into the thrust
    private const float RaiseLean = 0.04f;
    private const float ThrustLean = -0.12f;

    /// <summary>How much of the gesture's arm shows: eased in over the raise, full through the thrust and recoil,
    /// eased out over the return; 0 with zero slope at both ends.</summary>
    internal static float Weight(float t)
    {
        t = MathUtils.Clamp01(t);
        if (t < RaiseEnd) return MathUtils.SmoothStep(t / RaiseEnd);
        if (t < RecoilEnd) return 1f;
        return 1f - MathUtils.SmoothStep((t - RecoilEnd) / (1f - RecoilEnd));
    }

    /// <summary>The right shoulder's target at progress <paramref name="t"/>.</summary>
    internal static float Shoulder(float t) => Target(t, RaisedShoulder, ThrustShoulder, RecoilShoulder);

    /// <summary>The right elbow's target at progress <paramref name="t"/>.</summary>
    internal static float Elbow(float t) => Target(t, RaisedElbow, ThrustElbow, RecoilElbow);

    // Each phase starts where the last one ended, so the target is continuous
    private static float Target(float t, float raised, float thrust, float recoil)
    {
        t = MathUtils.Clamp01(t);
        if (t < RaiseEnd) return raised;
        if (t < ThrustEnd) return MathUtils.Lerp(raised, thrust, MathUtils.EaseOutCubic((t - RaiseEnd) / (ThrustEnd - RaiseEnd)));
        if (t < RecoilEnd) return MathUtils.Lerp(thrust, recoil, MathUtils.SmoothStep((t - ThrustEnd) / (RecoilEnd - ThrustEnd)));
        return recoil;
    }

    /// <summary>The torso pitch the gesture adds at progress <paramref name="t"/> (negative leans forward);
    /// continuous and 0 at both ends.</summary>
    internal static float Lean(float t)
    {
        t = MathUtils.Clamp01(t);
        float shape;
        if (t < RaiseEnd) shape = RaiseLean;
        else if (t < ThrustEnd) shape = MathUtils.Lerp(RaiseLean, ThrustLean, MathUtils.SmoothStep((t - RaiseEnd) / (ThrustEnd - RaiseEnd)));
        else shape = MathUtils.Lerp(ThrustLean, 0f, MathUtils.SmoothStep((t - ThrustEnd) / (1f - ThrustEnd)));
        return Weight(t) * shape;
    }

    /// <summary>Lays the gesture's right arm over <paramref name="pose"/>; every other joint is left as it is.</summary>
    internal static Pose Apply(in Pose pose, float t)
    {
        var weight = Weight(t);
        if (weight <= 0.001f) return pose;
        var shoulder = MathUtils.Lerp(pose.RightShoulder, Shoulder(t), weight);
        var elbow = MathUtils.Lerp(pose.RightElbow, Elbow(t), weight);
        return new Pose(pose.LeftHip, pose.RightHip, pose.LeftKnee, pose.RightKnee,
            pose.LeftShoulder, shoulder, pose.LeftElbow, elbow, pose.TorsoPitch, pose.Bob);
    }
}
