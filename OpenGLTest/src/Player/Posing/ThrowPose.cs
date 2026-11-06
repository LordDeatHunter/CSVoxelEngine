using Pose = VoxelEngine.Player.PlayerModel.Pose;

namespace VoxelEngine.Player.Posing;

/// <summary>The right arm's throw and timed-use overrides: the cocked arm, its strike and settle, the raised use pose, and how much of the cocked arm frame shows.</summary>
internal sealed class ThrowPose
{
    private readonly PlayerModel _model;

    internal ThrowPose(PlayerModel model) => _model = model;

    internal const float ThrowCockedShoulder = 1.3f;
    internal const float ThrowCockedElbow = 0.6f;
    // The timed-use pose's raised arm: up in front of the chest with the elbow bent, presenting
    // the held item (between the equip hold and the throw's cock)
    internal const float UseShoulder = 1.0f;
    internal const float UseElbow = 1.15f;
    internal const float ThrowFollowShoulder = 1.6f;
    internal const float ThrowFollowElbow = 0.5f;
    internal const float ThrowStrikeEnd = 0.3f;

    // How much of the cocked arm the frame shows: eased in with the charge, full while held, and
    // handed to the strike (the base pose racing to the follow-through) over the release.
    internal float ThrowArmWeight()
    {
        if (_model.ThrowCharge.HasValue)
            return MathUtils.SmoothStep(_model.ThrowCharge.Value);
        if (_model.ThrowProgress >= ThrowStrikeEnd)
            return 0f;
        var u = _model.ThrowProgress / ThrowStrikeEnd;
        return MathUtils.SmoothStep(_model.ThrowReleaseCharge) * (1f - u * u);
    }

    // The timed-use pose: the hand comes up in front of the chest, presenting what it holds,
    // rises through the hold (UseShoulder/UseElbow) and eases back when the charge ends. One
    // generic pose for every use time; a strike, a wrap and a sprinkle can follow.
    internal Pose WithUse(in Pose pose)
    {
        // A bow draws through BowPose, not the raised presenting pose
        if (!_model.UseCharge.HasValue || _model.UsePoseActive == Content.Blocks.UsePose.Bow) return pose;
        var raised = MathUtils.SmoothStep(_model.UseCharge.Value);
        var fromShoulder = MathUtils.Lerp(pose.RightShoulder, UseShoulder, raised);
        var fromElbow = MathUtils.Lerp(pose.RightElbow, UseElbow, raised);
        return new Pose(pose.LeftHip, pose.RightHip, pose.LeftKnee, pose.RightKnee,
            pose.LeftShoulder, fromShoulder, pose.LeftElbow, fromElbow, pose.TorsoPitch, pose.Bob);
    }

    internal Pose WithThrow(in Pose pose)
    {
        var cocked = 0f;
        if (_model.ThrowCharge.HasValue)
            cocked = MathUtils.SmoothStep(_model.ThrowCharge.Value);
        else if (_model.ThrowProgress < 1f)
            cocked = MathUtils.SmoothStep(_model.ThrowReleaseCharge);
        if (cocked <= 0f)
            return pose;
        var fromShoulder = MathUtils.Lerp(pose.RightShoulder, ThrowCockedShoulder, cocked);
        var fromElbow = MathUtils.Lerp(pose.RightElbow, ThrowCockedElbow, cocked);
        if (_model.ThrowCharge.HasValue || _model.ThrowProgress >= 1f)
            return new Pose(pose.LeftHip, pose.RightHip, pose.LeftKnee, pose.RightKnee,
                pose.LeftShoulder, fromShoulder, pose.LeftElbow, fromElbow,
                pose.TorsoPitch, pose.Bob);
        float swingShoulder, swingElbow;
        if (_model.ThrowProgress < ThrowStrikeEnd)
        {
            var u = _model.ThrowProgress / ThrowStrikeEnd;
            var strike = u * u;
            swingShoulder = MathUtils.Lerp(fromShoulder, ThrowFollowShoulder, strike);
            swingElbow = MathUtils.Lerp(fromElbow, ThrowFollowElbow, strike);
        }
        else
        {
            var u = (_model.ThrowProgress - ThrowStrikeEnd) / (1f - ThrowStrikeEnd);
            var settle = 1f - (1f - u) * (1f - u);
            swingShoulder = MathUtils.Lerp(ThrowFollowShoulder, pose.RightShoulder, settle);
            swingElbow = MathUtils.Lerp(ThrowFollowElbow, pose.RightElbow, settle);
        }
        return new Pose(pose.LeftHip, pose.RightHip, pose.LeftKnee, pose.RightKnee,
            pose.LeftShoulder, swingShoulder, pose.LeftElbow, swingElbow,
            pose.TorsoPitch, pose.Bob);
    }
}
