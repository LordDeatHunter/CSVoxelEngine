using System;
using Pose = VoxelEngine.Player.PlayerModel.Pose;

namespace VoxelEngine.Player.Posing;

/// <summary>The swim stroke's arm-out shape and the swim and surface-float leg pose laid over the base pose.</summary>
internal sealed class SwimPose
{
    private readonly PlayerModel _model;

    internal SwimPose(PlayerModel model) => _model = model;

    // Surface float (a body at rest at the top of deep water, WaterPhase.Float): upright and straight,
    // the arms sculling slowly beside the hips (upper arms a little out and forward, forearms bent
    // forward, the hands sweeping in and out together) and the legs hanging with a faint flutter. It
    // eases in over the swim pose at FloatBlendRate and gives way to the swim pose during a stroke.
    internal const float FloatBlendRate = 5f;
    internal const float FloatArmRoll = 0.38f;
    internal const float FloatArmFlex = 0.15f;
    internal const float FloatForearmFlex = 0.85f;
    internal const float FloatScullYaw = 0.32f;
    internal const float FloatScullFlex = 0.12f;
    internal const float FloatScullRate = 2.1f;
    internal const float FloatLegHip = 0.06f;
    internal const float FloatLegKnee = -0.14f;
    internal const float FloatFlutterHip = 0.07f;
    internal const float FloatFlutterKnee = 0.08f;
    internal const float FloatFlutterRate = 3.3f;
    internal const float FloatBobAmp = 0.03f;
    internal const float FloatBobRate = 1.4f;

    // The stroke clock as three phases: the thrust out to the sides (fast), a short hold with the
    // arms out, and the slow recovery back to the front, after which the cycle rests until the next
    // stroke. StrokeOut is 0 with the arms in front and 1 with them out to the sides.
    private const float StrokeThrustEnd = 0.2f;
    private const float StrokeHoldEnd = 0.3f;

    internal float StrokeOut()
    {
        var t = _model.SwimStrokeProgress;
        if (t >= 1f) return 0f;
        if (t < StrokeThrustEnd) return MathUtils.EaseOutCubic(t / StrokeThrustEnd);
        if (t < StrokeHoldEnd) return 1f;
        return 1f - MathUtils.SmoothStep((t - StrokeHoldEnd) / (1f - StrokeHoldEnd));
    }

    // Swimming replaces the ground cycle for the legs and body: they kick at the burst (snapping
    // from tucked to straight with the thrust), then draw back up slowly while the arms recover.
    // The arms themselves are the arm frames below. Blended in by the smoothed swim amount, so
    // entering and leaving the water never snaps.
    internal Pose ApplySwim(in Pose pose)
    {
        var s = _model.SwimBlend;
        if (s <= 0.001f) return pose;
        var t = _model.SwimStrokeProgress;
        // 0 = legs straight (just kicked), 1 = drawn up under the body.
        float tuck;
        if (t >= 1f) tuck = 0.55f;
        else if (t < StrokeThrustEnd) tuck = MathUtils.Lerp(0.55f, 0f, MathUtils.EaseOutCubic(t / StrokeThrustEnd));
        else tuck = 0.55f * MathUtils.SmoothStep((t - StrokeThrustEnd) / (1f - StrokeThrustEnd));
        var hip = 0.1f + 0.5f * tuck;
        var knee = -(0.15f + 1.1f * tuck);
        var bob = MathF.Sin(_model.SwimBobTime * 1.7f) * 0.025f;

        // The surface float replaces the kick: the legs hang nearly straight under the upright body,
        // fluttering faintly out of step, and the body bobs on the swell.
        var f = FloatShare();
        var flutter = MathF.Sin(_model.SwimBobTime * FloatFlutterRate);
        var flutterKnee = MathF.Sin(_model.SwimBobTime * FloatFlutterRate + 0.9f);
        var leftHip = MathUtils.Lerp(hip + 0.08f, FloatLegHip + FloatFlutterHip * flutter, f);
        var rightHip = MathUtils.Lerp(hip - 0.08f, FloatLegHip - FloatFlutterHip * flutter, f);
        var leftKnee = MathUtils.Lerp(knee, FloatLegKnee - FloatFlutterKnee * (0.5f + 0.5f * flutterKnee), f);
        var rightKnee = MathUtils.Lerp(knee, FloatLegKnee - FloatFlutterKnee * (0.5f - 0.5f * flutterKnee), f);
        bob = MathUtils.Lerp(bob, MathF.Sin(_model.SwimBobTime * FloatBobRate) * FloatBobAmp, f);
        return new Pose(
            MathUtils.Lerp(pose.LeftHip, leftHip, s), MathUtils.Lerp(pose.RightHip, rightHip, s),
            MathUtils.Lerp(pose.LeftKnee, leftKnee, s), MathUtils.Lerp(pose.RightKnee, rightKnee, s),
            pose.LeftShoulder, pose.RightShoulder, pose.LeftElbow, pose.RightElbow,
            MathUtils.Lerp(pose.TorsoPitch, 0f, s), MathUtils.Lerp(pose.Bob, bob, s));
    }

    // How much of the water pose is the surface float rather than the swim, 0..1
    internal float FloatShare() => _model.SwimBlend > 0.001f ? MathUtils.Clamp01(_model.FloatBlend / _model.SwimBlend) : 0f;
}
