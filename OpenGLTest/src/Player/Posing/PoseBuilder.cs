using System;
using Pose = VoxelEngine.Player.PlayerModel.Pose;

namespace VoxelEngine.Player.Posing;

/// <summary>Assembles the frame's joint pose: the walk/run cycle and the jump pose blended by the air amount, then the swim, the secondary motion, the jab, the throw and the use, in that order.</summary>
internal sealed class PoseBuilder
{
    private readonly PlayerModel _model;

    internal PoseBuilder(PlayerModel model) => _model = model;

    // Gait tuning. WalkSpeedRef matches the camera's base move speed, so Blend hits 1
    // exactly when walking normally and stays capped while sprinting or flying.
    internal const float WalkSpeedRef = 8f;
    internal const float WalkSwingAmp = 0.65f;
    internal const float RunSwingAmp = 1.05f;
    internal const float WalkKneeAmp = 0.9f;
    internal const float RunKneeAmp = 1.5f;
    internal const float WalkElbowAmp = 0.45f;
    internal const float RunElbowAmp = 0.85f;
    internal const float WalkBobAmp = 0.04f;
    internal const float RunBobAmp = 0.075f;
    internal const float WalkTorsoPitch = 0.06f;
    internal const float RunTorsoPitch = 0.20f;
    // Extra stride rate on top of the speed-scaled walk cadence, so running reads as a
    // faster cadence and not just a sped-up walk loop.
    internal const float RunStrideBoost = 2.5f;
    // Global gauge on the whole stride cadence, walk and run alike. The speed-scaled
    // rate above sets how fast the cycle *should* read; 0.7 slows both cycles by 30%
    // without touching the swing amplitudes (the legs and arms travel the same arc,
    // just over a longer stride period).
    internal const float GaitSpeedScale = 0.5f;
    internal const float MoveBlendRate = 8f;
    internal const float RunBlendRate = 6f;
    internal const float AirBlendRate = 10f;
    // Airborne leg targets. A standing jump tucks both knees under; a sprint jump splits
    // into a reaching front leg and a tucked trailing leg. Rise tucks harder, fall extends
    // toward landing (all knees stay bent backward, hence negative with a floor).
    internal const float JumpHipFront = 0.75f;
    internal const float JumpHipBack = 0.30f;
    internal const float SprintJumpHipFront = 0.95f;
    internal const float SprintJumpHipBack = -0.30f;
    internal const float JumpKneeFront = -1.6f;
    internal const float JumpKneeBack = -1.3f;
    internal const float SprintJumpKneeFront = -1.0f;
    internal const float SprintJumpKneeBack = -1.8f;

    internal const float MinKneeBend = -0.08f;
    internal const float JumpShoulder = 0.5f;
    internal const float JumpElbow = 0.7f;
    internal const float SprintJumpShoulderFront = -0.3f;
    internal const float SprintJumpShoulderBack = 0.4f;
    internal const float JumpTorso = 0.02f;
    internal const float SprintJumpTorso = 0.30f;

    /// <param name="pitchDeg">The camera's look pitch, which the jab is thrown along and nothing
    /// else in the pose is: the hips, knees and torso keep facing the body's own way.</param>
    internal Pose GetPose(float pitchDeg)
    {
        var swingAmp = MathUtils.Lerp(WalkSwingAmp, RunSwingAmp, _model.RunBlend);
        var kneeAmp = MathUtils.Lerp(WalkKneeAmp, RunKneeAmp, _model.RunBlend);
        var elbowAmp = MathUtils.Lerp(WalkElbowAmp, RunElbowAmp, _model.RunBlend);
        var swing = MathF.Sin(_model.WalkPhase) * swingAmp * _model.MoveBlend;

        // Hips and shoulders swing opposite; knees flex (backward, hence negative) with a
        // phase lag so the joint reads instead of the leg swinging like a plank. Elbows
        // stay slightly bent at rest and flex forward as the arm swings.
        var leftKnee = -(Math.Max(0f, MathF.Sin(_model.WalkPhase - 0.7f)) * kneeAmp * _model.MoveBlend + 0.03f);
        var rightKnee = -(Math.Max(0f, MathF.Sin(_model.WalkPhase + MathF.PI - 0.7f)) * kneeAmp * _model.MoveBlend + 0.03f);
        var leftShoulder = PlayerModel.RestShoulderForward - swing + MathF.Sin(_model.PoseTime * 1.7f) * 0.02f;
        var rightShoulder = PlayerModel.RestShoulderForward + swing - MathF.Sin(_model.PoseTime * 1.7f) * 0.02f
            + _model.HoldBlend * PlayerModel.HoldShoulder;
        var leftElbow = (Math.Max(0f, MathF.Sin(_model.WalkPhase)) * elbowAmp * _model.MoveBlend) + PlayerModel.RestElbow;
        var rightElbow = (Math.Max(0f, -MathF.Sin(_model.WalkPhase)) * elbowAmp * _model.MoveBlend) + PlayerModel.RestElbow
            + _model.HoldBlend * PlayerModel.HoldElbow;

        var bobAmp = MathUtils.Lerp(WalkBobAmp, RunBobAmp, _model.RunBlend);
        var bob = MathF.Abs(MathF.Cos(_model.WalkPhase)) * bobAmp * _model.MoveBlend;
        var torsoPitch = -_model.MoveBlend * MathUtils.Lerp(WalkTorsoPitch, RunTorsoPitch, _model.RunBlend);
        var ground = new Pose(swing, -swing, leftKnee, rightKnee,
            leftShoulder, rightShoulder, leftElbow, rightElbow, torsoPitch, bob);

        // The punch rides on whichever pose the frame is in (ground cycle or jump), applied
        // after the two are blended so a swing still plays while running or falling. The swim
        // stroke and the body mods (landing, crouch, flinch, sway) sit between the two.
        if (_model.AirBlend <= 0.001f)
            return Finish(ground, pitchDeg);

        var rise = MathUtils.Clamp01(_model.AirVelocity / 11f);
        var fall = MathUtils.Clamp01(-_model.AirVelocity / 12f);
        var airLeftHip = MathUtils.Lerp(JumpHipFront, SprintJumpHipFront, _model.AirRun)
            + rise * 0.15f - fall * 0.35f;
        var airRightHip = MathUtils.Lerp(JumpHipBack, SprintJumpHipBack, _model.AirRun)
            - rise * 0.1f + fall * 0.35f;
        var airLeftKnee = Math.Min(MinKneeBend,
            MathUtils.Lerp(JumpKneeFront, SprintJumpKneeFront, _model.AirRun) - rise * 0.25f + fall * 0.7f);
        var airRightKnee = Math.Min(MinKneeBend,
            MathUtils.Lerp(JumpKneeBack, SprintJumpKneeBack, _model.AirRun) - rise * 0.2f + fall * 0.9f);
        var airLeftShoulder = MathUtils.Lerp(JumpShoulder, SprintJumpShoulderFront, _model.AirRun);
        var airRightShoulder = MathUtils.Lerp(JumpShoulder, SprintJumpShoulderBack, _model.AirRun)
            + _model.HoldBlend * PlayerModel.HoldShoulder;
        var airLeftElbow = JumpElbow;
        var airRightElbow = JumpElbow + _model.HoldBlend * PlayerModel.HoldElbow;
        var airTorso = -MathUtils.Lerp(JumpTorso, SprintJumpTorso, _model.AirRun);
        var air = new Pose(airLeftHip, airRightHip, airLeftKnee, airRightKnee,
            airLeftShoulder, airRightShoulder, airLeftElbow, airRightElbow, airTorso, 0f);

        var t = _model.AirBlend;
        var blended = new Pose(
            MathUtils.Lerp(ground.LeftHip, air.LeftHip, t),
            MathUtils.Lerp(ground.RightHip, air.RightHip, t),
            MathUtils.Lerp(ground.LeftKnee, air.LeftKnee, t),
            MathUtils.Lerp(ground.RightKnee, air.RightKnee, t),
            MathUtils.Lerp(ground.LeftShoulder, air.LeftShoulder, t),
            MathUtils.Lerp(ground.RightShoulder, air.RightShoulder, t),
            MathUtils.Lerp(ground.LeftElbow, air.LeftElbow, t),
            MathUtils.Lerp(ground.RightElbow, air.RightElbow, t),
            MathUtils.Lerp(ground.TorsoPitch, air.TorsoPitch, t),
            MathUtils.Lerp(ground.Bob, 0f, t));
        return Finish(blended, pitchDeg);
    }

    // Flight, then swim, then the body mods, the jab, the throw and the use over the base pose
    private Pose Finish(in Pose basePose, float pitchDeg) =>
        _model.ThrowArm.WithUse(_model.ThrowArm.WithThrow(_model.Jab.WithPunch(
            SecondaryMotion.ApplyMods(_model.Swim.ApplySwim(_model.Flight.Apply(_model.Bow.Apply(basePose))),
                _model.Secondary.ComputeMods()), pitchDeg)));
}
