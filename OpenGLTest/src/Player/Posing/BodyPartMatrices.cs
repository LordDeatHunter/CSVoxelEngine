using System;
using System.Numerics;

namespace VoxelEngine.Player.Posing;

/// <summary>The world matrices of the body parts (torso frame, head, two-segment arms riding the torso with their socket and pull, legs off the root) and the held item's hand anchor.</summary>
internal sealed class BodyPartMatrices
{
    private readonly PlayerModel _model;

    internal BodyPartMatrices(PlayerModel model) => _model = model;

    private const float PelvisFallback = 0.75f;

    /// <summary>The body of <see cref="PlayerModel.GetPartMatrix"/>.</summary>
    internal Matrix4x4 GetPartMatrix(string part, Vector3 eyePos, float yawDeg, float pitchDeg, float backOffset = 0f)
    {
        var pose = _model.GetPose(pitchDeg);
        var pitchRad = MathUtils.ToRadians(pitchDeg);
        var rig = _model.Rig;
        Vector3 Pivot(string name) => rig.Parts[name].Pivot;
        var feet = eyePos - new Vector3(0f, Camera.EyeHeight, 0f);
        if (backOffset != 0f)
        {
            var yawBase = yawDeg * MathF.PI / 180f;
            feet -= new Vector3(MathF.Cos(yawBase) * backOffset, 0f, MathF.Sin(yawBase) * backOffset);
        }
        // Model faces -Z at rest; this yaw maps it onto the camera's horizontal facing
        // (at the default -90 it is the identity, verified against Camera.GetForwardVector).
        // A lagging body faces its own yaw and the head makes up the difference.
        var mods = _model.Secondary.ComputeMods();
        // The blade swing in progress (a slice or a sword swing), or every term zero
        var blade = _model.Blade;
        var bodyYawDeg = _model.DrawnBodyYaw(yawDeg);
        var yawRad = -(bodyYawDeg + 90f) * MathF.PI / 180f;
        // Swimming tips the body forward about the hips, a death lays it over about the feet.
        var pelvisY = rig.Parts.TryGetValue("ThighL", out var thigh) ? thigh.Pivot.Y : PelvisFallback;
        var local = Matrix4x4.Identity;
        if (mods.SwimPitch != 0f || mods.SwimRoll != 0f)
            local = Matrix4x4.CreateTranslation(0f, -pelvisY, 0f) * Matrix4x4.CreateRotationX(mods.SwimPitch)
                    * Matrix4x4.CreateRotationZ(mods.SwimRoll) * Matrix4x4.CreateTranslation(0f, pelvisY, 0f);
        if (mods.DeathPitch != 0f)
            local *= Matrix4x4.CreateRotationX(mods.DeathPitch);
        var root = local * Matrix4x4.CreateRotationY(yawRad + mods.RootYaw) * Matrix4x4.CreateTranslation(feet + new Vector3(0f, pose.Bob, 0f));
        var swingAxis = Vector3.UnitX;

        // The torso frame: hip-origin, so it turns about the hips. The neck and both shoulders are its
        // children (their pivots are expressed relative to the hips), so whatever the torso does - lean,
        // twist, roll - they ride on it and can never come off it or sink into it.
        var torsoFrame = Matrix4x4.CreateFromAxisAngle(swingAxis, pose.TorsoPitch)
                         * Matrix4x4.CreateRotationY(mods.TorsoYaw) * Matrix4x4.CreateRotationZ(mods.TorsoRoll)
                         * Matrix4x4.CreateTranslation(Pivot("Torso")) * root;

        Matrix4x4 Torso() => torsoFrame;

        Matrix4x4 Head() =>
            Matrix4x4.CreateFromAxisAngle(swingAxis, pitchDeg * MathF.PI / 180f * 0.8f + mods.HeadPitch)
            * Matrix4x4.CreateRotationY(mods.HeadYaw)
            * Matrix4x4.CreateTranslation(Pivot("Head") - Pivot("Torso")) * torsoFrame;

        // The arm's two frames: the base flexion and yaw, then the pose-specific overlays.
        void ArmFrames(Vector3 pivot, float shoulder, float sweep, float elbow,
            out Quaternion upper, out Quaternion fore)
        {
            // Drift the hands toward the view center so they read at the bottom of the
            // frame instead of out at the edges. Right arm (+X) yaws one way, left the other.
            // The slice sweep rides that same yaw but is applied after the forward pitch
            // (System.Numerics is row-vector, so the leftmost factor runs first), carrying the
            // raised arm through a horizontal arc instead of twisting a hanging one.
            var side = pivot.X > 0f ? 1f : -1f;
            upper = LimbFrames.FrameOf(Matrix4x4.CreateFromAxisAngle(swingAxis, shoulder)
                            * Matrix4x4.CreateFromAxisAngle(Vector3.UnitY, side * PlayerModel.ArmInwardYaw + sweep + _model.SwayYaw));
            fore = LimbFrames.FrameOf(Matrix4x4.CreateFromAxisAngle(swingAxis, elbow));
            _model.Overlays.ApplyArmFrames(side, ref upper, ref fore);
            // The arm now hangs off the torso, so the torso's twist turns it too. The authored poses (a blade
            // swing's sweep, the cocked throw, the cast, the punch) were tuned in body space, so while one is showing the twist
            // is taken back out of the arm's yaw and its hand path stays where it was; an idle arm simply follows
            // the torso round.
            var authored = side > 0f ? MathUtils.Clamp01(MathF.Max(MathF.Max(blade.Stroke, CastMotion.Weight(_model.CastProgress)),
                MathF.Max(_model.ThrowArm.ThrowArmWeight(), _model.Jab.PunchActivity()))) : 0f;
            if (authored > 0f && mods.TorsoYaw != 0f)
                upper = LimbFrames.FrameOf(Matrix4x4.CreateFromQuaternion(upper) * Matrix4x4.CreateRotationY(-mods.TorsoYaw * authored));
        }

        // A shoulder sits where it rests on the torso, slid in toward the body as the arm lifts (the socket), plus its
        // pull toward the reaching arm, in the torso frame.
        Matrix4x4 UpperArm(Vector3 pivot, Quaternion upper) =>
            Matrix4x4.CreateFromQuaternion(upper)
            * Matrix4x4.CreateTranslation(pivot - Pivot("Torso") + _model.ShoulderPull(pivot.X > 0f)
                + new Vector3(pivot.X > 0f ? -1f : 1f, 0f, 0f) * LimbFrames.ShoulderSocketInward * LimbFrames.SocketWeight(upper)) * torsoFrame;

        // The legs turn back against the body's twist so the feet stay planted.
        Matrix4x4 Thigh(Vector3 pivot, float hip) =>
            Matrix4x4.CreateFromAxisAngle(swingAxis, hip)
            * Matrix4x4.CreateRotationY(-mods.RootYaw * SliceMotion.SliceLegCounter)
            * Matrix4x4.CreateTranslation(pivot) * root;

        // The jab swings the arm across to the aim's own side as it extends, and back out on the
        // recovery. PunchArmYaw is absolute while UpperArm adds ArmInwardYaw on its own, so that
        // fixed term is taken back out here.
        var rightArmYaw = blade.ArmYaw + _model.Jab.PunchArmYaw(pitchRad, pose.TorsoPitch) - PlayerModel.ArmInwardYaw;
        // Through a blade swing the arm is eased from the carrying pose into the swing's own (the slice's
        // extended arm, the sword's raised or dropped one) by the stroke.
        var rightShoulder = MathUtils.Lerp(pose.RightShoulder, blade.Shoulder, blade.Stroke);
        var rightElbow = MathUtils.Lerp(pose.RightElbow, blade.Elbow, blade.Stroke);

        // Lowering the hand by translating the arm would detach the shoulder from the torso
        // (only visible in third person), so instead keep the shoulder fixed and re-solve the
        // shoulder pitch and elbow that land the hand at the same lowered spot.
        if (blade.HandLift != 0f)
        {
            ArmSolver.ArmHandOffset(rightShoulder, rightElbow, out var handY, out var handZ);
            (rightShoulder, rightElbow) = ArmSolver.SolveArm(handY + blade.HandLift, handZ);
        }

        // Child joints ride their parent: the elbow offset is the file's forearm pivot minus
        // its upper-arm pivot, so reshaped limbs keep working.
        Quaternion upperL = default, foreL = default, upperR = default, foreR = default;
        if (part is "UpperArmL" or "ForearmL")
            ArmFrames(Pivot("UpperArmL"), pose.LeftShoulder, 0f, pose.LeftElbow, out upperL, out foreL);
        else if (part is "UpperArmR" or "ForearmR")
            ArmFrames(Pivot("UpperArmR"), rightShoulder, rightArmYaw, rightElbow, out upperR, out foreR);

        return part switch
        {
            "Torso" => Torso(),
            "Head" => Head(),
            "UpperArmL" => UpperArm(Pivot("UpperArmL"), upperL),
            "UpperArmR" => UpperArm(Pivot("UpperArmR"), upperR),
            "ForearmL" => Matrix4x4.CreateFromQuaternion(foreL)
                * Matrix4x4.CreateTranslation(Pivot("ForearmL") - Pivot("UpperArmL"))
                * UpperArm(Pivot("UpperArmL"), upperL),
            "ForearmR" => Matrix4x4.CreateFromQuaternion(foreR)
                * Matrix4x4.CreateTranslation(Pivot("ForearmR") - Pivot("UpperArmR"))
                * UpperArm(Pivot("UpperArmR"), upperR),
            "ThighL" => Thigh(Pivot("ThighL"), pose.LeftHip),
            "ThighR" => Thigh(Pivot("ThighR"), pose.RightHip),
            "ShinL" => Matrix4x4.CreateFromAxisAngle(swingAxis, pose.LeftKnee)
                * Matrix4x4.CreateTranslation(Pivot("ShinL") - Pivot("ThighL"))
                * Thigh(Pivot("ThighL"), pose.LeftHip),
            "ShinR" => Matrix4x4.CreateFromAxisAngle(swingAxis, pose.RightKnee)
                * Matrix4x4.CreateTranslation(Pivot("ShinR") - Pivot("ThighR"))
                * Thigh(Pivot("ThighR"), pose.RightHip),
            _ => Matrix4x4.Identity,
        };
    }

    /// <summary>The body of <see cref="PlayerModel.GetHandAnchorMatrix"/>.</summary>
    internal Matrix4x4 GetHandAnchorMatrix(Vector3 eyePos, float yawDeg, float backOffset = 0f, float pitchDeg = 0f)
    {
        var forearm = GetPartMatrix("ForearmR", eyePos, yawDeg, pitchDeg, backOffset);
        var fist = Vector3.Transform(new Vector3(0f, _model.HandDepth(), 0f), forearm);
        var yawRad = -(_model.DrawnBodyYaw(yawDeg) + 90f) * MathF.PI / 180f;
        var pitchRad = MathUtils.ToRadians(pitchDeg);
        // The jab is thrown along the aim rather than the body, so the tool takes the look pitch
        // and the jab's swing for the length of the swing, then drops both as the arm settles.
        var punch = _model.Jab.PunchActivity();
        var rotation = Matrix4x4.CreateRotationX(pitchRad * punch)
                       * Matrix4x4.CreateRotationY(yawRad + _model.Jab.PunchArmYaw(pitchRad, _model.GetPose(pitchDeg).TorsoPitch) + _model.SwayYaw);

        // A blade swing carries the blade by the wrist: its tip points along the forearm (the item's tip is its local +Y,
        // the forearm's hand end its local -Y), cocked up a little and rolled about its length, and it takes over from
        // the body-space orientation as the stroke ramps in, so the blade follows the hand's sweep instead of a
        // separate formula and never turns up in the middle of the cut.
        var blade = _model.Blade;
        if (blade.Stroke > 0f)
        {
            var forearmRotation = forearm;
            forearmRotation.Translation = Vector3.Zero;
            var wrist = Matrix4x4.CreateRotationX(MathF.PI) * Matrix4x4.CreateRotationY(blade.BladeRoll)
                        * Matrix4x4.CreateRotationX(blade.BladeCant) * forearmRotation
                        * Matrix4x4.CreateRotationY(blade.BladeYaw);
            rotation = Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(LimbFrames.FrameOf(rotation), LimbFrames.FrameOf(wrist), blade.Stroke));
        }

        return rotation * Matrix4x4.CreateTranslation(fist);
    }
}
