using System;
using System.Numerics;

namespace VoxelEngine.Player.Posing;

/// <summary>Lays the pose-specific arm frames (jump, swim stroke and float, punch chamber, cocked throw) over the base arm frames.</summary>
internal sealed class ArmOverlays
{
    private readonly PlayerModel _model;

    internal ArmOverlays(PlayerModel model) => _model = model;

    // Swim stroke arm poses (ApplyArmFrames): reaching forward with the hands together, and thrust
    // out to the sides, slightly behind the shoulders.
    private const float SwimReachRoll = 0.12f;
    private const float SwimReachFlex = 1.75f;
    private const float SwimSideRoll = 1.5f;
    private const float SwimSideFlex = 0f;
    private const float SwimSideBehind = 0.3f;

    // Airborne arms are thrown out to the sides (abduction about the body's front axis) with a
    // slight forward flex and a soft elbow, instead of hanging forward. A sprint jump trails them
    // further back and lower.
    private const float AirArmRoll = 0.95f;
    private const float SprintAirArmRoll = 0.6f;
    private const float AirArmFlex = 0.12f;
    private const float SprintAirArmFlex = -0.3f;
    private const float AirElbowFlex = 0.35f;
    private const float SprintAirElbowFlex = 0.25f;

    // The chambered punch: the upper arm flares out to the side and forward and the forearm is folded
    // across so the wrist points to the front; the thrust then brings the elbow in and forward.
    private const float PunchChamberRoll = 1.2f;
    private const float PunchChamberYaw = 0.35f;
    private const float PunchChamberForearm = 1.15f;

    // The cocked pose proper (ApplyArmFrames): the upper arm out to the right and a little behind
    // the body, the forearm bent up at about a right angle to it, wrist above the elbow, and the
    // shoulders wound back toward the throwing side. These replace the old straight-up arm.
    private const float ThrowArmRoll = 1.4f;
    private const float ThrowArmFlex = 0.1f;
    private const float ThrowArmBehind = 0.4f;
    private const float ThrowForearmTilt = 0.3f;
    private const float ThrowForearmUp = 1.45f;

    /// <summary>Lays the pose-specific arm frames over the base ones; <paramref name="side"/> is +1 right, -1 left.</summary>
    internal void ApplyArmFrames(float side, ref Quaternion upper, ref Quaternion fore)
    {
        var right = side > 0f;

        // Jumping: arms out to the sides. A carried item keeps the right arm nearer the front.
        var air = _model.AirBlend * (1f - _model.SwimBlend) * (right ? 1f - 0.6f * _model.HoldBlend : 1f);
        if (air > 0.001f)
        {
            var rise = MathUtils.Clamp01(_model.AirVelocity / 11f);
            var fall = MathUtils.Clamp01(-_model.AirVelocity / 12f);
            var roll = MathUtils.Lerp(AirArmRoll, SprintAirArmRoll, _model.AirRun) + (rise - fall * 0.3f) * 0.25f;
            LimbFrames.Blend(ref upper, LimbFrames.UpperFrame(side, roll, MathUtils.Lerp(AirArmFlex, SprintAirArmFlex, _model.AirRun), 0f), air);
            LimbFrames.Blend(ref fore, LimbFrames.ForeFrame(side, MathUtils.Lerp(AirElbowFlex, SprintAirElbowFlex, _model.AirRun), 0f), air);
        }

        // Swimming: the arms reach out in front, thrust out to the sides at the burst (a little behind
        // the shoulders), hold there for a beat and creep back to the front for the next stroke.
        if (_model.SwimBlend > 0.001f)
        {
            var out_ = _model.Swim.StrokeOut();
            var sculling = MathF.Sin(_model.SwimBobTime * 2.3f) * 0.07f * (1f - out_);
            var reachRoll = SwimReachRoll;
            var roll = MathUtils.Lerp(reachRoll, SwimSideRoll, out_);
            var flex = MathUtils.Lerp(SwimReachFlex, SwimSideFlex, out_) + sculling;
            var yaw = MathUtils.Lerp(0f, -SwimSideBehind, out_);
            LimbFrames.Blend(ref upper, LimbFrames.UpperFrame(side, roll, flex, yaw), _model.SwimBlend);
            LimbFrames.Blend(ref fore, LimbFrames.ForeFrame(side, MathUtils.Lerp(0.1f, 0.3f, out_), 0f), _model.SwimBlend);

            // Floating: the arms scull beside the hips instead, the forearms bent forward and the
            // hands sweeping out and back in together (the yaw mirrors with the side)
            var floatW = _model.FloatBlend;
            if (floatW > 0.001f)
            {
                var scull = MathF.Sin(_model.SwimBobTime * SwimPose.FloatScullRate);
                var scullFlex = MathF.Cos(_model.SwimBobTime * SwimPose.FloatScullRate) * SwimPose.FloatScullFlex;
                LimbFrames.Blend(ref upper, LimbFrames.UpperFrame(side, SwimPose.FloatArmRoll, SwimPose.FloatArmFlex + scullFlex, SwimPose.FloatScullYaw * scull), floatW);
                LimbFrames.Blend(ref fore, LimbFrames.ForeFrame(side, SwimPose.FloatForearmFlex, 0f), floatW);
            }
        }

        if (!right) return;

        // Punch: the flare is full at the chamber and gone again at full extension, where the IK
        // frame (the fist on the aim line) takes over, so the elbow swings from out to the side to
        // forward as the arm thrusts.
        if (_model.MineT < 1f)
        {
            var w = PunchPose.PunchWeights(_model.MineT);
            var flare = w.Retract * (1f - w.Extend) * (1f - w.Recover);
            LimbFrames.Blend(ref upper, LimbFrames.UpperFrame(side, PunchChamberRoll, 0f, PunchChamberYaw), flare);
            LimbFrames.Blend(ref fore, LimbFrames.ForeFrame(side, PunchChamberForearm, 0.1f), flare);
        }

        // Throw: the cocked arm out to the right and behind, forearm up.
        var cock = _model.ThrowArm.ThrowArmWeight();
        if (cock > 0.001f)
        {
            LimbFrames.Blend(ref upper, LimbFrames.UpperFrame(side, ThrowArmRoll, ThrowArmFlex, -ThrowArmBehind), cock);
            LimbFrames.Blend(ref fore, LimbFrames.ForeFrame(side, ThrowForearmTilt, ThrowForearmUp), cock);
        }
    }
}
