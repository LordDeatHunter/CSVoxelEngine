using System;
using Pose = VoxelEngine.Player.PlayerModel.Pose;

namespace VoxelEngine.Player.Posing;

/// <summary>The body-wide secondary motion laid over the pose: landing dip, crouch, flinch, sway, equip dip, the blade swings' and the throw's twist and lean, the swim tilt, head turn and death fall.</summary>
internal sealed class SecondaryMotion
{
    private readonly PlayerModel _model;

    internal SecondaryMotion(PlayerModel model) => _model = model;

    private const float ThrowWindTwist = 0.55f;
    private const float ThrowFollowTwist = 0.3f;

    internal PoseMods ComputeMods()
    {
        var dip = Math.Clamp(_model.LandingDip, -0.15f, 0.6f);
        var crouch = _model.CrouchBlend;
        var ground = 1f - _model.AirBlend;
        var move = _model.MoveBlend * ground * (1f - _model.SwimBlend);

        // A blade swing (a slice or a sword swing): the body turns and the shoulders twist with the sweep, it
        // rolls toward the swing's side, leans over the blow, and the weight sinks into the strike (knees and hips
        // bend and the torso dips toward it).
        var blade = _model.Blade;

        // Throw: the shoulders wind back toward the throwing (right) side and lean back while the
        // arm is cocked, then whip forward through the release and settle.
        float throwLean = 0f;
        float throwTwist = 0f;
        if (_model.ThrowCharge.HasValue)
        {
            var cocked = MathUtils.SmoothStep(_model.ThrowCharge.Value);
            throwLean = 0.18f * cocked;
            throwTwist = -ThrowWindTwist * cocked;
        }
        else if (_model.ThrowProgress < 1f)
        {
            var cocked = MathUtils.SmoothStep(_model.ThrowReleaseCharge);
            var strike = MathUtils.EaseOutCubic(MathF.Min(1f, _model.ThrowProgress / ThrowPose.ThrowStrikeEnd));
            var settle = 1f - MathUtils.SmoothStep((_model.ThrowProgress - ThrowPose.ThrowStrikeEnd) / (1f - ThrowPose.ThrowStrikeEnd));
            throwLean = MathUtils.Lerp(0.18f * cocked, -0.3f, strike) * settle;
            throwTwist = MathUtils.Lerp(-ThrowWindTwist * cocked, ThrowFollowTwist, strike) * settle;
        }

        var fall = _model.DeathFall;
        return new PoseMods
        {
            HipBend = dip * 0.55f + crouch * 0.7f + blade.Dip * 0.25f,
            KneeBend = dip * 1.1f + crouch * 1.4f + blade.Dip * 0.4f,
            RootYaw = blade.RootYaw,
            Shoulders = _model.SwayPitch + dip * 0.3f,
            RightShoulderExtra = -_model.EquipLowering * 0.9f,
            TorsoPitch = -dip * 0.2f - crouch * 0.25f + _model.Flinch * 0.3f + throwLean - blade.Dip * 0.22f + blade.Lean
                         + _model.Bow.TorsoLean(),
            Drop = dip * 0.12f + crouch * 0.17f + blade.Dip * 0.06f,
            TorsoYaw = MathF.Sin(_model.WalkPhase) * 0.1f * move + blade.TorsoTwist + throwTwist + _model.Bow.TorsoTwist()
                       - PunchPose.PunchTwist * PunchPose.PunchWeights(_model.MineT).Extend,
            // The body also leans a little toward whichever side its shoulders are being pulled out to.
            TorsoRoll = MathF.Cos(_model.WalkPhase) * 0.035f * move + blade.TorsoRoll + _model.Flinch * 0.08f
                        - (_model.RightPullX + _model.LeftPullX) * 0.8f,
            HeadPitch = _model.Flinch * 0.45f - crouch * 0.1f,
            HeadYaw = _model.BodyYawLags && _model.LookKnown ? -MathUtils.ToRadians(PlayerModel.WrapDegrees(_model.LookYaw - _model.TrailingBodyYaw)) : 0f,
            // The whole body (legs included) tips toward the heading: forward when swimming ahead, back
            // when backing off, rolled toward a strafe, plus a lunge at the burst.
            // The surface float stands the body back up, so it carries none of either.
            SwimPitch = _model.SwimBlend * (1f - _model.Swim.FloatShare()) * (-0.25f - 0.85f * MathUtils.Clamp01(_model.SwimForward / 5f)
                                      + 0.45f * MathUtils.Clamp01(-_model.SwimForward / 5f)
                                      - 0.18f * MathUtils.Pulse(_model.SwimStrokeProgress, 0.08f)),
            SwimRoll = -_model.SwimBlend * (1f - _model.Swim.FloatShare()) * Math.Clamp(_model.SwimSide / 5f, -1f, 1f) * 0.6f,
            DeathPitch = MathUtils.EaseInCubic(MathUtils.Clamp01(fall)) * 1.5f,
        };
    }

    internal static Pose ApplyMods(in Pose pose, in PoseMods m) => new(
        pose.LeftHip + m.HipBend, pose.RightHip + m.HipBend,
        pose.LeftKnee - m.KneeBend, pose.RightKnee - m.KneeBend,
        pose.LeftShoulder + m.Shoulders, pose.RightShoulder + m.Shoulders + m.RightShoulderExtra,
        pose.LeftElbow, pose.RightElbow,
        pose.TorsoPitch + m.TorsoPitch, pose.Bob - m.Drop);
}
