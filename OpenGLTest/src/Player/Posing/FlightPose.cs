using System;
using VoxelEngine.Config;
using Pose = VoxelEngine.Player.PlayerModel.Pose;

namespace VoxelEngine.Player.Posing;

/// <summary>
/// The creative-flight pose laid over the ground and jump poses: the legs hang off the hips swaying a
/// little and trail bent back the faster the body flies, the torso leans into the flight and the arms
/// settle at rest instead of swinging with a walk cycle. Every amount is bounded by its
/// <c>PlayerConfig.Flight*</c> cap whatever the speed, and the whole pose eases in and out with
/// <see cref="PlayerModel.FlightBlend"/>.
/// </summary>
internal sealed class FlightPose
{
    private readonly PlayerModel _model;

    internal FlightPose(PlayerModel model) => _model = model;

    /// <summary>
    /// How strongly the flight shows its speed, 0 hovering to 1 at <see cref="PlayerConfig.FlightFullSpeed"/>
    /// and held there: it scales the trail, the lean and the sway.
    /// </summary>
    internal static float SpeedShare(float speed) => MathUtils.Clamp01(speed / PlayerConfig.FlightFullSpeed);

    internal Pose Apply(in Pose pose)
    {
        var b = _model.FlightBlend;
        if (b <= 0.001f) return pose;

        var share = MathUtils.SmoothStep(_model.FlightSpeedShare);
        // The legs trail back and fold at the knee with speed; a gentle sway runs out of step between
        // them, a little even when hovering
        var sway = MathF.Sin(_model.PoseTime * PlayerConfig.FlightSwayRate)
            * PlayerConfig.FlightSwayHip * (PlayerConfig.FlightHoverSway + (1f - PlayerConfig.FlightHoverSway) * share);
        var swayKnee = MathF.Sin(_model.PoseTime * PlayerConfig.FlightSwayRate + 1.1f) * PlayerConfig.FlightSwayKnee;
        var trailHip = -PlayerConfig.FlightTrailHip * share;
        var trailKnee = -(PlayerConfig.FlightRestKnee + PlayerConfig.FlightTrailKnee * share);
        var leftHip = trailHip + sway;
        var rightHip = trailHip - sway;
        var leftKnee = trailKnee - MathF.Abs(swayKnee) * (0.4f + 0.6f * share);
        var rightKnee = trailKnee - MathF.Abs(MathF.Sin(_model.PoseTime * PlayerConfig.FlightSwayRate + 1.1f + MathF.PI / 2f))
            * PlayerConfig.FlightSwayKnee * (0.4f + 0.6f * share);

        // The arms hang as they do standing, whatever the walk cycle was doing
        var restShoulder = PlayerModel.RestShoulderForward;
        var restElbow = PlayerModel.RestElbow;
        return new Pose(
            MathUtils.Lerp(pose.LeftHip, leftHip, b), MathUtils.Lerp(pose.RightHip, rightHip, b),
            MathUtils.Lerp(pose.LeftKnee, leftKnee, b), MathUtils.Lerp(pose.RightKnee, rightKnee, b),
            MathUtils.Lerp(pose.LeftShoulder, restShoulder, b),
            MathUtils.Lerp(pose.RightShoulder, restShoulder + _model.HoldBlend * PlayerModel.HoldShoulder, b),
            MathUtils.Lerp(pose.LeftElbow, restElbow, b),
            MathUtils.Lerp(pose.RightElbow, restElbow + _model.HoldBlend * PlayerModel.HoldElbow, b),
            MathUtils.Lerp(pose.TorsoPitch, -PlayerConfig.FlightLean * share, b),
            MathUtils.Lerp(pose.Bob, 0f, b));
    }
}
