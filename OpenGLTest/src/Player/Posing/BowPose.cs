using System;
using VoxelEngine.Content.Blocks;
using Pose = VoxelEngine.Player.PlayerModel.Pose;

namespace VoxelEngine.Player.Posing;

/// <summary>
/// A bow's two-handed draw (<see cref="UsePose.Bow"/>): the item arm (the right) holds the bow out
/// forward while the other arm draws the string back to the cheek as the charge rises, and the shoulders
/// turn side-on to the target. It holds at full draw; on release the drawing arm snaps back fast and the
/// rest of the body settles after it. The draw rides the timed-use charge (<see cref="PlayerModel.UseCharge"/>),
/// so the world and the debugger drive it exactly alike.
/// </summary>
internal sealed class BowPose
{
    private readonly PlayerModel _model;

    internal BowPose(PlayerModel model) => _model = model;

    /// <summary>Seconds the body takes to settle after a release; the drawing arm snaps back in a share of it.</summary>
    internal const float ReleaseSeconds = 0.4f;
    /// <summary>The share of <see cref="ReleaseSeconds"/> the drawing arm takes to snap back.</summary>
    internal const float SnapShare = 0.3f;
    /// <summary>The least charge a release is shown for; a tap that drew nothing just drops the arms.</summary>
    internal const float MinReleaseCharge = 0.1f;

    // The torso's turn at full draw, radians: the bow shoulder leads, the string shoulder goes back
    internal const float Twist = -0.55f;
    // The torso leans back a little into the draw
    internal const float Lean = 0.06f;

    // The bow arm: out forward at about shoulder height, a little off to the side, nearly straight
    internal const float BowArmRoll = 0.18f;
    internal const float BowArmFlex = 1.45f;
    internal const float BowArmYaw = -0.1f;
    internal const float BowForearmFlex = 0.06f;

    // The string arm: the upper arm out to the side and back, the forearm folded so the hand meets the cheek
    internal const float StringArmRoll = 1.3f;
    internal const float StringArmFlex = 0.9f;
    internal const float StringArmYaw = 1.0f;
    internal const float StringForearmFlex = 2.4f;
    internal const float StringForearmBend = 0.2f;

    private bool Drawing => _model.UsePoseActive == UsePose.Bow && _model.UseCharge.HasValue;

    /// <summary>How far the string arm is drawn, 0..1: with the charge while drawing, then snapping to 0 on release.</summary>
    internal float DrawWeight()
    {
        if (Drawing) return MathUtils.SmoothStep(_model.UseCharge!.Value);
        if (_model.BowReleaseProgress >= 1f) return 0f;
        var snap = MathUtils.EaseOutCubic(MathF.Min(1f, _model.BowReleaseProgress / SnapShare));
        return _model.BowReleaseCharge * (1f - snap);
    }

    /// <summary>How much of the bow arm and the side-on body shows, 0..1: with the charge while drawing,
    /// then easing out over the whole release, after the string arm.</summary>
    internal float BodyWeight()
    {
        if (Drawing) return MathUtils.SmoothStep(_model.UseCharge!.Value);
        if (_model.BowReleaseProgress >= 1f) return 0f;
        return _model.BowReleaseCharge * (1f - MathUtils.SmoothStep(_model.BowReleaseProgress));
    }

    /// <summary>The torso twist the draw adds, radians.</summary>
    internal float TorsoTwist() => Twist * BodyWeight();

    /// <summary>The torso pitch the draw adds (a little back).</summary>
    internal float TorsoLean() => Lean * BodyWeight();

    /// <summary>Lays the draw over the base joint pose: both shoulders and elbows head for the bow's (the arm frames do the rest).</summary>
    internal Pose Apply(in Pose pose)
    {
        var body = BodyWeight();
        var draw = DrawWeight();
        if (body <= 0.001f && draw <= 0.001f) return pose;
        return new Pose(pose.LeftHip, pose.RightHip, pose.LeftKnee, pose.RightKnee,
            MathUtils.Lerp(pose.LeftShoulder, StringArmFlex, draw), MathUtils.Lerp(pose.RightShoulder, BowArmFlex, body),
            MathUtils.Lerp(pose.LeftElbow, StringForearmFlex, draw), MathUtils.Lerp(pose.RightElbow, BowForearmFlex, body),
            pose.TorsoPitch, pose.Bob);
    }
}
