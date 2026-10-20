using System;
using System.Numerics;
using static VoxelEngine.Player.PlayerRig;
using Pose = VoxelEngine.Player.PlayerModel.Pose;

namespace VoxelEngine.Player.Posing;

/// <summary>The jab: its eased phase weights and the two-bone solve that drives the right fist along the aim, chambered then extended, with the torso lean and the off-hand counter.</summary>
internal sealed class PunchPose
{
    private readonly PlayerModel _model;

    internal PunchPose(PlayerModel model) => _model = model;

    // The mine/attack swing is a straight jab thrown down the crosshair, not the upward arc a
    // shoulder-only swing gives: the fist comes up to the aim line, drives out along it as the
    // elbow extends, then eases back to the carrying pose.
    //
    // Both fist positions are given in the view's own frame — X right of the aim, Y up from it,
    // Z out along it, in blocks from the eye — and the shoulder and elbow that reach one are
    // solved per frame from the camera's pitch (PunchTargetJoints). That is what makes the jab
    // follow the aim instead of the body: look straight up and the fist punches up, because the
    // target is fixed to the view while the joints chasing it are not. The joint angles come out
    // above the horizontal for a level look, which is not an uppercut: the eye sits 0.295 above
    // and 0.35 beside the shoulder, so a fist on the crosshair has to be raised above the
    // shoulder to get there.
    private static readonly Vector3 PunchChamberFist = new(0.29f, -0.05f, 0.28f);
    private static readonly Vector3 PunchExtendFist = new(0.15f, -0.02f, 0.46f);
    // Fractions of the swing: bring the fist to the aim, thrust it out to the impact, drift back.
    private const float PunchWindupEnd = 0.22f;
    internal const float PunchStrikeEnd = 0.55f;
    // How far the torso leans into the thrust.
    private const float PunchLean = 0.07f;
    // The off hand swings back as the fist drives out, and the shoulders twist into the blow.
    private const float PunchLeftCounter = 0.45f;
    internal const float PunchTwist = 0.16f;

    // The jab's eased phase weights, all zero at either end of the swing so the pose never jumps
    // when it starts, crosses the impact or finishes. Retract draws the fist up to the aim line
    // over the wind-up, Extend drives it out to full extension across the thrust and eases back
    // out over the recovery, and Recover settles the chambered pose back onto the base one. The
    // torso lean and the inward arm yaw both ride Extend, so they follow the fist out and back.
    internal static (float Retract, float Extend, float Recover) PunchWeights(float p)
    {
        if (p <= 0f || p >= 1f) return (0f, 0f, 0f);
        if (p < PunchWindupEnd)
            return (MathUtils.EaseOutCubic(p / PunchWindupEnd), 0f, 0f);
        if (p < PunchStrikeEnd)
            return (1f,
                MathUtils.EaseOutCubic((p - PunchWindupEnd) / (PunchStrikeEnd - PunchWindupEnd)), 0f);

        var settle = MathUtils.SmoothStep((p - PunchStrikeEnd) / (1f - PunchStrikeEnd));
        return (1f, 1f - settle, settle);
    }

    // The torso's rotation (about the hips) for a given forward pitch: what the shoulders ride on.
    private Matrix4x4 TorsoRotation(float torsoPitch)
    {
        var mods = _model.Secondary.ComputeMods();
        return Matrix4x4.CreateRotationX(torsoPitch) * Matrix4x4.CreateRotationY(mods.TorsoYaw)
               * Matrix4x4.CreateRotationZ(mods.TorsoRoll);
    }

    // A fist position in the view frame as an offset from the shoulder in body space. The shoulder
    // does not rotate with the look - it sits ShoulderX to the side, ShoulderY above the feet and
    // FirstPersonBodyBack behind the eye - so the view-frame point has to be turned back out by the
    // pitch before the arm can be solved to it. The shoulder is where the torso's lean and twist
    // actually carry it (it rides on the torso), not where it rests, or the fist would miss the aim by
    // the few centimeters the torso moved it. Y is the up axis and Z points forward out of the eye, so a
    // point in front has a negative Z here.
    private Vector3 PunchTargetFromShoulder(Vector3 viewFist, float pitchRad, float torsoPitch)
    {
        var cos = MathF.Cos(pitchRad);
        var sin = MathF.Sin(pitchRad);
        var hip = _model.Rig.Parts.TryGetValue("Torso", out var torso) ? torso.Pivot : new Vector3(0f, HipY, 0f);
        // The shoulder is slid in toward the body while the arm is lifted for the punch (the socket).
        var punchWeights = PunchWeights(_model.MineT);
        var socket = LimbFrames.ShoulderSocketInward * punchWeights.Retract * (1f - punchWeights.Recover);
        var shoulderNow = Vector3.Transform(new Vector3(ShoulderX - socket, ShoulderY, 0f) - hip, TorsoRotation(torsoPitch)) + hip;
        var target = new Vector3(
            viewFist.X,
            Camera.EyeHeight + viewFist.Y * cos + viewFist.Z * sin,
            viewFist.Y * sin - viewFist.Z * cos - PlayerModel.FirstPersonBodyBack);
        return target - shoulderNow;
    }

    // Shoulder and elbow that put the fist on a view-frame point, for a camera pitched by
    // pitchRad. Beyond the arm's reach the solver pins the elbow straight, which leaves the jab
    // pointing at the target and merely falling short of it.
    private (float Shoulder, float Elbow) PunchTargetJoints(Vector3 viewFist, float pitchRad, float torsoPitch)
    {
        var target = PunchTargetFromShoulder(viewFist, pitchRad, torsoPitch);
        var lateral = MathF.Sqrt(target.X * target.X + target.Z * target.Z);
        return ArmSolver.SolveArm(target.Y, -lateral);
    }

    // Azimuth of the fist on a view-frame point: the arm yaws to swing across to the aim's own
    // side of the shoulder. Absolute, so the caller subtracts the fixed ArmInwardYaw that
    // UpperArm adds by itself.
    private float PunchTargetYaw(Vector3 viewFist, float pitchRad, float torsoPitch)
    {
        var target = PunchTargetFromShoulder(viewFist, pitchRad, torsoPitch);
        return MathF.Atan2(-target.X, -target.Z);
    }

    // Right shoulder and elbow through the jab: chamber the fist against the aim line, drive it
    // out to full extension (the elbow is the joint that actually does the punching), then ease
    // back to the base pose.
    private (float Shoulder, float Elbow) PunchJoints(in Pose pose, float p, float pitchRad, float torsoPitch)
    {
        var w = PunchWeights(p);
        var (chamberShoulder, chamberElbow) = PunchTargetJoints(PunchChamberFist, pitchRad, torsoPitch);
        var (extendShoulder, extendElbow) = PunchTargetJoints(PunchExtendFist, pitchRad, torsoPitch);
        var shoulder = MathUtils.Lerp(pose.RightShoulder, chamberShoulder, w.Retract);
        var elbow = MathUtils.Lerp(pose.RightElbow, chamberElbow, w.Retract);
        shoulder = MathUtils.Lerp(shoulder, extendShoulder, w.Extend);
        elbow = MathUtils.Lerp(elbow, extendElbow, w.Extend);
        return (MathUtils.Lerp(shoulder, pose.RightShoulder, w.Recover),
            MathUtils.Lerp(elbow, pose.RightElbow, w.Recover));
    }

    // How much of the jab the frame is showing, 1 through the chamber and the thrust and easing
    // back to 0 over the recovery. The held item takes its share of the punch's orientation from
    // this, so the tool turns with the fist instead of staying level with the body.
    internal float PunchActivity()
    {
        if (_model.MineT >= 1f)
            return 0f;
        var w = PunchWeights(_model.MineT);
        return MathF.Max(w.Retract, w.Extend);
    }

    // The jab's own arm yaw in radians, absolute in body space, riding the same weights as the
    // joints. Depends only on the swing timer and the pitch, so GetPartMatrix and the hand anchor
    // can both ask for it without re-deriving the pose.
    internal float PunchArmYaw(float pitchRad, float torsoPitch)
    {
        if (_model.MineT >= 1f)
            return 0f;
        var w = PunchWeights(_model.MineT);
        var yaw = MathUtils.Lerp(0f, PunchTargetYaw(PunchChamberFist, pitchRad, torsoPitch), w.Retract);
        yaw = MathUtils.Lerp(yaw, PunchTargetYaw(PunchExtendFist, pitchRad, torsoPitch), w.Extend);
        return MathUtils.Lerp(yaw, 0f, w.Recover);
    }

    internal Pose WithPunch(in Pose pose, float pitchDeg)
    {
        if (_model.MineT >= 1f)
            return pose;
        var pitchRad = MathUtils.ToRadians(pitchDeg);
        var w = PunchWeights(_model.MineT);
        var torsoPitch = pose.TorsoPitch - PunchLean * (1f + _model.MineWeight * 1.5f) * w.Extend;
        var (shoulder, elbow) = PunchJoints(pose, _model.MineT, pitchRad, torsoPitch);
        // The arm hangs off the torso, so the torso's lean tilts it too: take that out of the flexion so the
        // fist still lands where the solve put it.
        shoulder -= torsoPitch * w.Retract * (1f - w.Recover);
        return new Pose(pose.LeftHip, pose.RightHip, pose.LeftKnee, pose.RightKnee,
            pose.LeftShoulder - PunchLeftCounter * w.Extend, shoulder, pose.LeftElbow, elbow,
            torsoPitch, pose.Bob);
    }
}
