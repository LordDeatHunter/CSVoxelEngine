using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace silkgltriangle;

/// <summary>
/// Blocky Minecraft-like player model with two-segment limbs: upper arm + forearm and
/// thigh + shin instead of Minecraft's single-piece limbs. The shape (part pivots, cubes,
/// skin) comes from a <see cref="PlayerRig"/> — loaded from the Blockbench project at
/// assets/models/player/player.bbmodel when present, built-in Steve otherwise — while the
/// posing itself stays procedural below. The model faces -Z at yaw -90, i.e. the same
/// direction the camera looks at spawn.
/// </summary>
public class PlayerModel
{
    // Classic Steve layout, in blocks. Legs 0.75 + torso 0.625 + head 0.425 = 1.8.
    public const float TotalHeight = 1.8f;
    public const float HipY = 0.75f;
    public const float TorsoWidth = 0.5f;
    public const float TorsoHeight = 0.625f;
    public const float TorsoDepth = 0.25f;
    public const float NeckY = 1.375f;
    public const float HeadSize = 0.425f;
    // One width for all four limb segments (upper arm, forearm, thigh, shin), so the
    // limbs can never drift to different thicknesses. Slimmer than Steve's 0.25.
    public const float LimbWidth = 0.2f;
    public const float UpperArmLength = 0.375f;
    public const float ForearmLength = 0.375f;
    public const float ThighLength = 0.375f;
    public const float ShinLength = 0.375f;
    public const float ShoulderY = 1.325f;
    // Derived from the widths above: arms hang flush with the torso sides, legs flush
    // with each other under the torso.
    public const float ShoulderX = TorsoWidth / 2f + LimbWidth / 2f;
    public const float HipX = LimbWidth / 2f;

    // Palette swatches (see PalettePixels): solid Blockbench-style colours behind one UV map.
    public const int Skin = 0;
    public const int Shirt = 1;
    public const int Pants = 2;
    public const int Hair = 3;
    public const int Shoe = 4;
    public const int PaletteCols = 4;
    public const int PaletteRows = 2;

    // Gait tuning. WalkSpeedRef matches the camera's base move speed, so Blend hits 1
    // exactly when walking normally and stays capped while sprinting or flying.
    private const float WalkSpeedRef = 8f;
    private const float WalkSwingAmp = 0.65f;
    private const float RunSwingAmp = 1.05f;
    private const float WalkKneeAmp = 0.9f;
    private const float RunKneeAmp = 1.5f;
    private const float WalkElbowAmp = 0.45f;
    private const float RunElbowAmp = 0.85f;
    private const float WalkBobAmp = 0.04f;
    private const float RunBobAmp = 0.075f;
    private const float WalkTorsoPitch = 0.06f;
    private const float RunTorsoPitch = 0.20f;
    // Extra stride rate on top of the speed-scaled walk cadence, so running reads as a
    // faster cadence and not just a sped-up walk loop.
    private const float RunStrideBoost = 2.5f;
    // Global gauge on the whole stride cadence, walk and run alike. The speed-scaled
    // rate above sets how fast the cycle *should* read; 0.7 slows both cycles by 30%
    // without touching the swing amplitudes (the legs and arms travel the same arc,
    // just over a longer stride period).
    private const float GaitSpeedScale = 0.5f;
    private const float MoveBlendRate = 8f;
    private const float RunBlendRate = 6f;
    private const float AirBlendRate = 10f;
    // Airborne leg targets. A standing jump tucks both knees under; a sprint jump splits
    // into a reaching front leg and a tucked trailing leg. Rise tucks harder, fall extends
    // toward landing (all knees stay bent backward, hence negative with a floor).
    private const float JumpHipFront = 0.45f;
    private const float JumpHipBack = -0.30f;
    private const float SprintJumpHipFront = 0.75f;
    private const float SprintJumpHipBack = -0.65f;
    private const float JumpKneeFront = -1.1f;
    private const float JumpKneeBack = -0.6f;
    private const float SprintJumpKneeFront = -0.45f;
    private const float SprintJumpKneeBack = -1.4f;
    private const float MinKneeBend = -0.08f;
    private const float JumpShoulder = 0.5f;
    private const float JumpElbow = 0.7f;
    private const float SprintJumpShoulderFront = -0.3f;
    private const float SprintJumpShoulderBack = 0.4f;
    private const float JumpTorso = 0.02f;
    private const float SprintJumpTorso = 0.30f;
    public const float MineDuration = 0.25f;

    // Slice attack (items with AttackPattern.Slice). One swing lasts SliceDuration and is a
    // wind-up, a strike and a recovery, all eased so the pose equals the idle hold at both ends
    // (no snap). The strike sweeps the raised right arm from the wind-up side through the aim to
    // the far side, while the blade rolls from upright to horizontal-outward.
    // SliceImpactProgress is where the sweep crosses the aim, which is also where the swing's
    // damage lands.
    public const float SliceDuration = 0.7f;

    /// <summary>Progress through a slice swing at which it crosses the aim and the damage lands.</summary>
    public const float SliceImpactProgress = 0.375f;

    // The wind-up cocks the arm further out than the strike carries it back, so the swing reads
    // as a wide arc; SliceSweepRadians is the follow-through past the aim.
    private const float SliceWindupRadians = 1.55f;
    private const float SliceSweepRadians = 1.25f;
    private const float SliceLiftRadians = 0.7f;
    private const float SliceRollRadians = 1.6f;
    // The forward lift would carry the hand up to eye height; the hand is solved this many blocks
    // lower at the strike (via SolveArm, which keeps the shoulder anchored) so the slice reads at
    // chest height instead.
    private const float SliceDropBlocks = 0.35f;
    // Wind-up (0..WindupEnd) reaches the start side; the strike (..StrikeEnd) crosses to the far
    // side; the rest is the recovery back to the idle pose.
    private const float SliceWindupEnd = 0.15f;
    private const float SliceStrikeEnd = 0.6f;

    // Charged throw (right-click in the air with a throwable). While held, the right arm
    // rises forward-up overhead into a cocked pre-throw with the elbow extending on the
    // way up, eased by the charge amount so the raise reads as slow. The cock goes the
    // forward way (never backward): swinging back would drag the hand down past the leg
    // and leave it hanging behind the back. Absolute joint targets (not deltas) so the
    // pose is the same held or empty-handed. On release the arm strikes fast from the
    // exact release pose through a follow-through, then settles slowly back to rest —
    // one direction change, which reads as follow-through rather than a bounce.
    public const float ThrowDuration = 0.55f;
    private const float ThrowCockedShoulder = 2.6f;
    private const float ThrowCockedElbow = 0.35f;
    private const float ThrowFollowShoulder = 1.6f;
    private const float ThrowFollowElbow = 0.5f;
    private const float ThrowStrikeEnd = 0.3f;

    // First-person framing: straight-hanging arms sit ~66 degrees below the view centre,
    // invisible until you pitch well down, and hide behind the chest when you do. Resting
    // them slightly forward with bent elbows and a touch of inward yaw parks the hands in
    // front of the chest, visible at the bottom of the view. Positive X rotation is
    // forward (the model faces -Z, so +pitch moves a hanging hand toward -Z).
    public const float RestShoulderForward = 0.25f;
    public const float RestElbow = 0.30f;
    public const float ArmInwardYaw = 0.12f;

    // Carrying pose: with something held, the right arm rises so the hand (and the item)
    // sits up in view instead of hanging hidden inside the arm's silhouette.
    public const float HoldShoulder = 0.5f;
    public const float HoldElbow = 1.0f;

    private float _walkPhase;
    private float _moveBlend;
    private float _runBlend;
    private float _airBlend;
    private float _airRun;
    private float _airVelocity;
    private float _mineT = 1f;
    private float _sliceT = 1f;
    private int _sliceDir;
    private float _throwT = 1f;
    private float? _throwCharge01;
    private float _throwReleaseCharge;
    private float _time;
    private float _holdBlend;

    /// <summary>Whether the right hand currently holds something; eases the carrying pose in and out.</summary>
    public bool HoldingItem { get; set; }

    /// <summary>Smoothed 0..1 carrying amount.</summary>
    public float HoldBlend => _holdBlend;

    /// <summary>The shape being posed: pivots, cubes and skin. Swapped for the file rig at startup.</summary>
    public PlayerRig Rig { get; set; } = PlayerRig.BuiltIn();

    /// <summary>0 when a mine hit just landed, 1 when the hand is back at rest.</summary>
    public float MineT => _mineT;

    /// <summary>Whether a slice swing is currently playing.</summary>
    public bool IsSlicing => _sliceT < 1f;

    /// <summary>Whether a throw swing is currently playing.</summary>
    public bool IsThrowing => _throwT < 1f;

    /// <summary>Progress of the active throw swing: 0 at release, 1 when it has finished.</summary>
    public float ThrowProgress => _throwT;

    /// <summary>Charge the arm is cocked to, or null when no throw is being aimed.</summary>
    public float? ThrowCharge => _throwCharge01;

    /// <summary>Progress of the active slice swing: 0 at the wind-up, 1 when it has finished.</summary>
    public float SliceProgress => _sliceT;

    /// <summary>Smoothed 0..1 movement amount driving the swing amplitude.</summary>
    public float MoveBlend => _moveBlend;

    /// <summary>Smoothed 0..1 running amount driving the larger run-cycle amplitudes.</summary>
    public float RunBlend => _runBlend;

    /// <summary>Smoothed 0..1 airborne amount blending the ground cycle into the jump pose.</summary>
    public float AirBlend => _airBlend;

    /// <summary>
    /// Advances the walk cycle and the mine swing. <paramref name="horizontalSpeed"/> is in
    /// blocks/s (the camera's XZ displacement per second); standing still eases the limbs
    /// back to rest instead of snapping. <paramref name="sprinting"/> selects the larger
    /// run cycle, and <paramref name="grounded"/> gates both it and the jump pose — callers
    /// pass true while swimming or flying so those stay out of the air pose.
    /// <paramref name="verticalVelocity"/> in blocks/s shapes the jump: rising tucks the
    /// knees, falling extends toward landing.
    /// </summary>
    public void Update(float deltaTime, float horizontalSpeed, bool sprinting = false, bool grounded = true,
        float verticalVelocity = 0f)
    {
        if (deltaTime <= 0f) return;
        _time += deltaTime;

        var target = MathUtils.Clamp01(horizontalSpeed / WalkSpeedRef);
        _moveBlend = MathUtils.Damp(_moveBlend, target, MoveBlendRate, deltaTime);
        var runTarget = sprinting && grounded && horizontalSpeed > 0.5f ? 1f : 0f;
        _runBlend = MathUtils.Damp(_runBlend, runTarget, RunBlendRate, deltaTime);
        _airBlend = MathUtils.Damp(_airBlend, grounded ? 0f : 1f, AirBlendRate, deltaTime);
        _airRun = MathUtils.Damp(_airRun, sprinting && horizontalSpeed > 0.5f ? 1f : 0f,
            RunBlendRate, deltaTime);
        _airVelocity = verticalVelocity;
        _holdBlend = MathUtils.Damp(_holdBlend, HoldingItem ? 1f : 0f, MoveBlendRate, deltaTime);

        // Stride rate grows with speed so feet roughly keep up with the ground, plus an
        // extra kick while running so the cadence lifts beyond a sped-up walk.
        _walkPhase += deltaTime * GaitSpeedScale * (4f + horizontalSpeed * 0.9f + _runBlend * RunStrideBoost);

        if (_mineT < 1f)
            _mineT = Math.Min(1f, _mineT + deltaTime / MineDuration);

        if (_sliceT < 1f)
            _sliceT = Math.Min(1f, _sliceT + deltaTime / SliceDuration);

        if (_throwT < 1f)
            _throwT = Math.Min(1f, _throwT + deltaTime / ThrowDuration);
    }

    /// <summary>Starts one hand swing (a mining hit just landed).</summary>
    public void Punch() => _mineT = 0f;

    /// <summary>
    /// Starts a slice swing: <paramref name="direction"/> is +1 for right-to-left and -1 for the
    /// left-to-right return. The arm's sweep and lift and the blade roll ride
    /// <see cref="SliceProgress"/>.
    /// </summary>
    public void StartSlice(int direction)
    {
        _sliceDir = direction;
        _sliceT = 0f;
    }

    public void StartThrowCharge() => _throwCharge01 = 0f;

    public void UpdateThrowCharge(float charge01)
    {
        if (_throwCharge01.HasValue)
            _throwCharge01 = MathUtils.Clamp01(charge01);
    }

    public void CancelThrowCharge() => _throwCharge01 = null;

    public void ReleaseThrow(float charge01)
    {
        _throwReleaseCharge = MathUtils.Clamp01(charge01);
        _throwCharge01 = null;
        _throwT = 0f;
    }

    // Signed yaw the right arm sweeps through: 0 at rest, out to the swing's start side on the
    // wind-up, across the aim to the far side on the strike, then eased back to 0 on the recovery
    // so the arm never snaps back to its idle pose. The sign follows the swing's direction, so
    // the return mirrors the first swing.
    private float SliceSweep()
    {
        var p = MathUtils.Clamp01(_sliceT);
        if (p <= 0f || p >= 1f) return 0f;

        var startSide = -SliceWindupRadians * _sliceDir;
        var farSide = SliceSweepRadians * _sliceDir;
        if (p < SliceWindupEnd)
            return MathUtils.Lerp(0f, startSide, MathUtils.SmoothStep(p / SliceWindupEnd));
        if (p < SliceStrikeEnd)
            return MathUtils.Lerp(startSide, farSide,
                MathUtils.SmoothStep((p - SliceWindupEnd) / (SliceStrikeEnd - SliceWindupEnd)));
        return MathUtils.Lerp(farSide, 0f,
            MathUtils.SmoothStep((p - SliceStrikeEnd) / (1f - SliceStrikeEnd)));
    }

    // One-shot 0 -> 1 -> 0 across the swing, peaking at the strike: it drives the arm lift and
    // the blade roll, and settles both back to the carrying pose by the end. It is 0 at both
    // ends, so the swing never jumps when it starts or finishes.
    private float SliceStroke() => MathUtils.Pulse(_sliceT, SliceImpactProgress);

    private float SliceLift() => SliceLiftRadians * SliceStroke();

    // Downward Y translation applied to the right arm at the strike, tied to the same 0->1->0
    // stroke so the arm is back at its idle height by the end (no snap).
    private float SliceDrop() => -SliceDropBlocks * SliceStroke();

    // Hand offset from the shoulder in the arm's bend plane (y, z), before the sweep yaw, for a
    // given shoulder pitch and elbow. Matches GetPartMatrix's arm chain exactly, so the two-bone
    // solve below can invert it.
    private static void ArmHandOffset(float shoulder, float elbow, out float y, out float z)
    {
        var a = ForearmLength * MathF.Cos(elbow) + UpperArmLength;
        var b = ForearmLength * MathF.Sin(elbow);
        y = -a * MathF.Cos(shoulder) + b * MathF.Sin(shoulder);
        z = -(a * MathF.Sin(shoulder) + b * MathF.Cos(shoulder));
    }

    // Two-bone solve: the shoulder pitch and elbow that reach a hand offset (y, z) with the
    // shoulder itself fixed. Lets the slice drop the hand by re-posing the arm instead of
    // translating it, which would detach the shoulder from the torso (visible in third person).
    private static (float Shoulder, float Elbow) SolveArm(float y, float z)
    {
        var span = UpperArmLength + ForearmLength;
        var reach = MathF.Min(MathF.Sqrt(y * y + z * z), span - 1e-4f);
        var elbow = 2f * MathF.Acos(Math.Clamp(reach / span, 0f, 1f));
        var a = ForearmLength * MathF.Cos(elbow) + UpperArmLength;
        var b = ForearmLength * MathF.Sin(elbow);
        var det = a * a + b * b;
        return (MathF.Atan2((b * y - a * z) / det, -(a * y + b * z) / det), elbow);
    }

    // Wrist twist: rolls the blade from standing upright at rest to laying flat (a touch past
    // horizontal) at the strike. The direction flips with the swing, so the return rolls the
    // blade 180 degrees the other way and the same edge leads both ways. The negative sign is
    // because the body's -Z rotation tips +Y toward -X; combined with the direction it lays the
    // blade outward on the swing's own side. Applied in model space, before the body yaw, so the
    // roll stays fixed to the body whichever way the player faces.
    private float SliceRoll() => -SliceRollRadians * _sliceDir * SliceStroke();

    /// <summary>Joint angles in radians plus the vertical bob, for this frame.</summary>
    public readonly struct Pose
    {
        public readonly float LeftHip;
        public readonly float RightHip;
        public readonly float LeftKnee;
        public readonly float RightKnee;
        public readonly float LeftShoulder;
        public readonly float RightShoulder;
        public readonly float LeftElbow;
        public readonly float RightElbow;
        public readonly float TorsoPitch;
        public readonly float Bob;

        public Pose(float leftHip, float rightHip, float leftKnee, float rightKnee,
            float leftShoulder, float rightShoulder, float leftElbow, float rightElbow,
            float torsoPitch, float bob)
        {
            LeftHip = leftHip;
            RightHip = rightHip;
            LeftKnee = leftKnee;
            RightKnee = rightKnee;
            LeftShoulder = leftShoulder;
            RightShoulder = rightShoulder;
            LeftElbow = leftElbow;
            RightElbow = rightElbow;
            TorsoPitch = torsoPitch;
            Bob = bob;
        }
    }

    public Pose GetPose()
    {
        var swingAmp = MathUtils.Lerp(WalkSwingAmp, RunSwingAmp, _runBlend);
        var kneeAmp = MathUtils.Lerp(WalkKneeAmp, RunKneeAmp, _runBlend);
        var elbowAmp = MathUtils.Lerp(WalkElbowAmp, RunElbowAmp, _runBlend);
        var swing = MathF.Sin(_walkPhase) * swingAmp * _moveBlend;

        // Hips and shoulders swing opposite; knees flex (backward, hence negative) with a
        // phase lag so the joint reads instead of the leg swinging like a plank. Elbows
        // stay slightly bent at rest and flex forward as the arm swings.
        var leftKnee = -(Math.Max(0f, MathF.Sin(_walkPhase - 0.7f)) * kneeAmp * _moveBlend + 0.03f);
        var rightKnee = -(Math.Max(0f, MathF.Sin(_walkPhase + MathF.PI - 0.7f)) * kneeAmp * _moveBlend + 0.03f);
        var leftShoulder = RestShoulderForward - swing + MathF.Sin(_time * 1.7f) * 0.02f;
        var rightShoulder = RestShoulderForward + swing - MathF.Sin(_time * 1.7f) * 0.02f
            + _holdBlend * HoldShoulder;
        var leftElbow = (Math.Max(0f, MathF.Sin(_walkPhase)) * elbowAmp * _moveBlend) + RestElbow;
        var rightElbow = (Math.Max(0f, -MathF.Sin(_walkPhase)) * elbowAmp * _moveBlend) + RestElbow
            + _holdBlend * HoldElbow;

        // Mining raises the right hand up-forward into view, peaking mid-swing, on top of
        // whatever the walk cycle is doing. sin(pi*t) is 0 at both ends, 1 in the middle.
        // Kept below the carrying pose's range so the two never stack past the vertical.
        // Applied by WithMinePump to the pose this method returns, not just the ground pose,
        // so a swing still plays while the player is jumping or falling.
        var pump = _mineT < 1f ? MathF.Sin(MathF.PI * _mineT) : 0f;

        var bobAmp = MathUtils.Lerp(WalkBobAmp, RunBobAmp, _runBlend);
        var bob = MathF.Abs(MathF.Cos(_walkPhase)) * bobAmp * _moveBlend;
        var torsoPitch = -_moveBlend * MathUtils.Lerp(WalkTorsoPitch, RunTorsoPitch, _runBlend);
        var ground = new Pose(swing, -swing, leftKnee, rightKnee,
            leftShoulder, rightShoulder, leftElbow, rightElbow, torsoPitch, bob);

        if (_airBlend <= 0.001f)
            return WithThrow(WithMinePump(ground, pump));

        var rise = MathUtils.Clamp01(_airVelocity / 11f);
        var fall = MathUtils.Clamp01(-_airVelocity / 12f);
        var airLeftHip = MathUtils.Lerp(JumpHipFront, SprintJumpHipFront, _airRun)
            + rise * 0.15f - fall * 0.35f;
        var airRightHip = MathUtils.Lerp(JumpHipBack, SprintJumpHipBack, _airRun)
            - rise * 0.1f + fall * 0.35f;
        var airLeftKnee = Math.Min(MinKneeBend,
            MathUtils.Lerp(JumpKneeFront, SprintJumpKneeFront, _airRun) - rise * 0.25f + fall * 0.7f);
        var airRightKnee = Math.Min(MinKneeBend,
            MathUtils.Lerp(JumpKneeBack, SprintJumpKneeBack, _airRun) - rise * 0.2f + fall * 0.9f);
        var airLeftShoulder = MathUtils.Lerp(JumpShoulder, SprintJumpShoulderFront, _airRun);
        var airRightShoulder = MathUtils.Lerp(JumpShoulder, SprintJumpShoulderBack, _airRun)
            + _holdBlend * HoldShoulder;
        var airLeftElbow = JumpElbow;
        var airRightElbow = JumpElbow + _holdBlend * HoldElbow;
        var airTorso = -MathUtils.Lerp(JumpTorso, SprintJumpTorso, _airRun);
        var air = new Pose(airLeftHip, airRightHip, airLeftKnee, airRightKnee,
            airLeftShoulder, airRightShoulder, airLeftElbow, airRightElbow, airTorso, 0f);

        var t = _airBlend;
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
        return WithThrow(WithMinePump(blended, pump));
    }

    private Pose WithThrow(in Pose pose)
    {
        var cocked = 0f;
        if (_throwCharge01.HasValue)
            cocked = MathUtils.SmoothStep(_throwCharge01.Value);
        else if (_throwT < 1f)
            cocked = MathUtils.SmoothStep(_throwReleaseCharge);
        if (cocked <= 0f)
            return pose;
        var fromShoulder = MathUtils.Lerp(pose.RightShoulder, ThrowCockedShoulder, cocked);
        var fromElbow = MathUtils.Lerp(pose.RightElbow, ThrowCockedElbow, cocked);
        if (_throwCharge01.HasValue || _throwT >= 1f)
            return new Pose(pose.LeftHip, pose.RightHip, pose.LeftKnee, pose.RightKnee,
                pose.LeftShoulder, fromShoulder, pose.LeftElbow, fromElbow,
                pose.TorsoPitch, pose.Bob);
        float swingShoulder, swingElbow;
        if (_throwT < ThrowStrikeEnd)
        {
            var u = _throwT / ThrowStrikeEnd;
            var strike = u * u;
            swingShoulder = MathUtils.Lerp(fromShoulder, ThrowFollowShoulder, strike);
            swingElbow = MathUtils.Lerp(fromElbow, ThrowFollowElbow, strike);
        }
        else
        {
            var u = (_throwT - ThrowStrikeEnd) / (1f - ThrowStrikeEnd);
            var settle = 1f - (1f - u) * (1f - u);
            swingShoulder = MathUtils.Lerp(ThrowFollowShoulder, pose.RightShoulder, settle);
            swingElbow = MathUtils.Lerp(ThrowFollowElbow, pose.RightElbow, settle);
        }
        return new Pose(pose.LeftHip, pose.RightHip, pose.LeftKnee, pose.RightKnee,
            pose.LeftShoulder, swingShoulder, pose.LeftElbow, swingElbow,
            pose.TorsoPitch, pose.Bob);
    }

    private static Pose WithMinePump(in Pose pose, float pump)
    {
        if (pump <= 0f)
            return pose;
        return new Pose(pose.LeftHip, pose.RightHip, pose.LeftKnee, pose.RightKnee,
            pose.LeftShoulder, pose.RightShoulder + pump, pose.LeftElbow,
            pose.RightElbow + pump * 0.4f, pose.TorsoPitch - pump * 0.03f, pose.Bob);
    }

    /// <summary>
    /// World matrices for every part, keyed by mesh name (see <see cref="MeshNames"/>).
    /// Thigh/Shin/UpperArm/Forearm entries are one side's; the caller draws each twice
    /// with the Left/Right matrices. <paramref name="eyePos"/> is the camera eye (feet are
    /// <see cref="Camera.EyeHeight"/> below it), <paramref name="yawDeg"/>/<paramref name="pitchDeg"/>
    /// the camera look. <paramref name="backOffset"/> slides the whole body away from the
    /// facing direction (first-person framing; the shadow pass leaves it at 0).
    /// </summary>
    public Matrix4x4 GetPartMatrix(string part, Vector3 eyePos, float yawDeg, float pitchDeg, float backOffset = 0f)
    {
        var pose = GetPose();
        var rig = Rig;
        Vector3 Pivot(string name) => rig.Parts[name].Pivot;
        var feet = eyePos - new Vector3(0f, Camera.EyeHeight, 0f);
        if (backOffset != 0f)
        {
            var yawBase = yawDeg * MathF.PI / 180f;
            feet -= new Vector3(MathF.Cos(yawBase) * backOffset, 0f, MathF.Sin(yawBase) * backOffset);
        }
        // Model faces -Z at rest; this yaw maps it onto the camera's horizontal facing
        // (at the default -90 it is the identity, verified against Camera.GetForwardVector).
        var yawRad = -(yawDeg + 90f) * MathF.PI / 180f;
        var root = Matrix4x4.CreateRotationY(yawRad) * Matrix4x4.CreateTranslation(feet + new Vector3(0f, pose.Bob, 0f));
        var swingAxis = Vector3.UnitX;

        Matrix4x4 Torso() =>
            Matrix4x4.CreateFromAxisAngle(swingAxis, pose.TorsoPitch)
            * Matrix4x4.CreateTranslation(Pivot("Torso")) * root;

        Matrix4x4 Head() =>
            Matrix4x4.CreateFromAxisAngle(swingAxis, pitchDeg * MathF.PI / 180f * 0.8f)
            * Matrix4x4.CreateTranslation(Pivot("Head")) * root;

        Matrix4x4 UpperArm(Vector3 pivot, float shoulder, float sweep)
        {
            // Drift the hands toward the view centre so they read at the bottom of the
            // frame instead of out at the edges. Right arm (+X) yaws one way, left the other.
            // The slice sweep rides that same yaw but is applied after the forward pitch
            // (System.Numerics is row-vector, so the leftmost factor runs first), carrying the
            // raised arm through a horizontal arc instead of twisting a hanging one.
            var inward = (pivot.X > 0f ? 1f : -1f) * ArmInwardYaw;
            return Matrix4x4.CreateFromAxisAngle(swingAxis, shoulder)
                * Matrix4x4.CreateFromAxisAngle(Vector3.UnitY, inward + sweep)
                * Matrix4x4.CreateTranslation(pivot) * root;
        }

        Matrix4x4 Thigh(Vector3 pivot, float hip) =>
            Matrix4x4.CreateFromAxisAngle(swingAxis, hip)
            * Matrix4x4.CreateTranslation(pivot) * root;

        var sliceSweep = SliceSweep();
        var rightShoulder = pose.RightShoulder + SliceLift();
        var rightElbow = pose.RightElbow;

        // Lowering the slice by translating the arm would detach the shoulder from the torso
        // (only visible in third person), so instead keep the shoulder fixed and re-solve the
        // shoulder pitch and elbow that land the hand at the same lowered spot.
        var sliceDrop = SliceDrop();
        if (sliceDrop != 0f)
        {
            ArmHandOffset(rightShoulder, rightElbow, out var handY, out var handZ);
            (rightShoulder, rightElbow) = SolveArm(handY + sliceDrop, handZ);
        }

        // Child joints ride their parent: the elbow offset is the file's forearm pivot minus
        // its upper-arm pivot, so reshaped limbs keep working.
        return part switch
        {
            "Torso" => Torso(),
            "Head" => Head(),
            "UpperArmL" => UpperArm(Pivot("UpperArmL"), pose.LeftShoulder, 0f),
            "UpperArmR" => UpperArm(Pivot("UpperArmR"), rightShoulder, sliceSweep),
            "ForearmL" => Matrix4x4.CreateFromAxisAngle(swingAxis, pose.LeftElbow)
                * Matrix4x4.CreateTranslation(Pivot("ForearmL") - Pivot("UpperArmL"))
                * UpperArm(Pivot("UpperArmL"), pose.LeftShoulder, 0f),
            "ForearmR" => Matrix4x4.CreateFromAxisAngle(swingAxis, rightElbow)
                * Matrix4x4.CreateTranslation(Pivot("ForearmR") - Pivot("UpperArmR"))
                * UpperArm(Pivot("UpperArmR"), rightShoulder, sliceSweep),
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

    /// <summary>
    /// How far below the right forearm's pivot the hand sits: the lowest cuboid bottom, so
    /// reshaped forearms carry the grip with them. Negative, in blocks.
    /// </summary>
    public float HandDepth()
    {
        var depth = 0f;
        foreach (var cube in Rig.Parts["ForearmR"].Cubes)
            depth = Math.Min(depth, cube.Min.Y);
        return depth;
    }

    /// <summary>
    /// Anchor for a held item: positioned at the right fist so it follows the walk swing
    /// and the mine pump, but oriented in body space. Parenting the orientation to the
    /// forearm would force absurd display angles the moment the carrying pose raises the
    /// arm (a level tool would need -130 degrees of pitch), so the JSON numbers stay human.
    /// </summary>
    public Matrix4x4 GetHandAnchorMatrix(Vector3 eyePos, float yawDeg, float backOffset = 0f)
    {
        var fist = Vector3.Transform(new Vector3(0f, HandDepth(), 0f),
            GetPartMatrix("ForearmR", eyePos, yawDeg, 0f, backOffset));
        var yawRad = -(yawDeg + 90f) * MathF.PI / 180f;
        // The slice sweep and blade roll ride the anchor as well as the fist position, so the held
        // blade turns through the arc and rolls from upright to horizontal instead of only sliding
        // along it. The roll is applied first (model space), then the body yaw and sweep, so its
        // axis stays fixed to the body.
        return Matrix4x4.CreateRotationZ(SliceRoll())
            * Matrix4x4.CreateRotationY(yawRad + SliceSweep())
            * Matrix4x4.CreateTranslation(fist);
    }

    /// <summary>Mesh name per draw; arms/legs draw one mesh twice (left + right matrices).</summary>
    public static readonly string[] BodyParts =
        ["Torso", "UpperArmL", "UpperArmR", "ForearmL", "ForearmR", "ThighL", "ThighR", "ShinL", "ShinR"];

    // First-person draws everything but the head. The torso only reads when the eye sits
    // forward of the chest plane (like real eyes forward of the sternum), so the whole
    // body shifts back a touch in the main pass: looking down then shows the shirt front
    // cascading into legs and feet instead of the neck. The shift must clear half the
    // torso depth (0.125) or the straight-down ray still lands on the collar. Shadows
    // stay rooted at the true feet so they never detach from the ground.
    public const float FirstPersonBodyBack = 0.2f;

    public static readonly string[] FirstPersonParts =
        ["Torso", "UpperArmL", "UpperArmR", "ForearmL", "ForearmR", "ThighL", "ThighR", "ShinL", "ShinR"];

    /// <summary>
    /// The shape being posed: one skin texture plus a pivot and pivot-relative cuboids per
    /// part, keyed by the <see cref="BodyParts"/> names. Either parsed out of the Blockbench
    /// project (<see cref="FromBbmodel"/>) or the built-in Steve (<see cref="BuiltIn"/>).
    /// </summary>
    public sealed class PlayerRig
    {
        public byte[] TexturePixels = [];
        public int TextureWidth;
        public int TextureHeight;
        public readonly Dictionary<string, RigPart> Parts = new(StringComparer.Ordinal);

        public sealed class RigPart
        {
            public Vector3 Pivot;
            public readonly List<RigCube> Cubes = new();
        }

        public sealed class RigCube
        {
            public Vector3 Min, Max;
            public UvRect?[] Faces = new UvRect?[6];
        }

        /// <summary>Steve with two-segment limbs, matching the historical hardcoded model.</summary>
        public static PlayerRig BuiltIn()
        {
            var rig = new PlayerRig
            {
                TexturePixels = PalettePixels(),
                TextureWidth = PaletteCols,
                TextureHeight = PaletteRows,
            };

            void Add(string name, Vector3 pivot, Vector3 min, Vector3 max, int[] swatches)
            {
                var part = new RigPart { Pivot = pivot };
                var faces = new UvRect?[6];
                for (var f = 0; f < 6; f++)
                    faces[f] = Cell(swatches[f]);
                part.Cubes.Add(new RigCube { Min = min, Max = max, Faces = faces });
                rig.Parts[name] = part;
            }

            var hw = LimbWidth * 0.5f;
            Add("Torso", new Vector3(0f, HipY, 0f),
                new Vector3(-TorsoWidth * 0.5f, 0f, -TorsoDepth * 0.5f),
                new Vector3(TorsoWidth * 0.5f, TorsoHeight, TorsoDepth * 0.5f), TorsoSwatches);
            var hs = HeadSize * 0.5f;
            Add("Head", new Vector3(0f, NeckY, 0f),
                new Vector3(-hs, 0f, -hs), new Vector3(hs, HeadSize, hs), HeadSwatches);
            foreach (var side in new[] { ("L", -1f), ("R", 1f) })
            {
                var ax = ShoulderX * side.Item2;
                Add("UpperArm" + side.Item1, new Vector3(ax, ShoulderY, 0f),
                    new Vector3(-hw, -UpperArmLength, -hw), new Vector3(hw, 0f, hw), UpperArmSwatches);
                Add("Forearm" + side.Item1, new Vector3(ax, ShoulderY - UpperArmLength, 0f),
                    new Vector3(-hw, -ForearmLength, -hw), new Vector3(hw, 0f, hw), ForearmSwatches);
                var hx = HipX * side.Item2;
                Add("Thigh" + side.Item1, new Vector3(hx, HipY, 0f),
                    new Vector3(-hw, -ThighLength, -hw), new Vector3(hw, 0f, hw), ThighSwatches);
                Add("Shin" + side.Item1, new Vector3(hx, HipY - ThighLength, 0f),
                    new Vector3(-hw, -ShinLength, -hw), new Vector3(hw, 0f, hw), ShinSwatches);
            }
            return rig;
        }

        /// <summary>Maps parsed file data onto the rig; a file without a texture keeps the palette.</summary>
        public static PlayerRig FromBbmodel(BbmodelData data)
        {
            var rig = new PlayerRig();
            if (data.TexturePixels != null)
            {
                rig.TexturePixels = data.TexturePixels;
                rig.TextureWidth = data.TextureWidth;
                rig.TextureHeight = data.TextureHeight;
            }
            else
            {
                rig.TexturePixels = PalettePixels();
                rig.TextureWidth = PaletteCols;
                rig.TextureHeight = PaletteRows;
            }

            foreach (var (name, part) in data.Parts)
            {
                var rigPart = new RigPart { Pivot = part.Pivot };
                foreach (var cube in part.Cubes)
                    rigPart.Cubes.Add(new RigCube { Min = cube.Min, Max = cube.Max, Faces = cube.Faces });
                rig.Parts[name] = rigPart;
            }
            return rig;
        }

        private static UvRect Cell(int swatch)
        {
            var col = swatch % PaletteCols;
            var row = swatch / PaletteCols;
            return new UvRect(new Vector2(col / (float)PaletteCols, row / (float)PaletteRows),
                new Vector2((col + 1) / (float)PaletteCols, (row + 1) / (float)PaletteRows));
        }
    }

    /// <summary>
    /// The 4x2 solid-colour palette texture (top-first RGBA): skin, shirt, pants, hair,
    /// shoe, then white spares. Every cuboid face UVs to one swatch centre, so the model
    /// is UV-mapped Blockbench-style with zero image assets to author.
    /// </summary>
    public static byte[] PalettePixels()
    {
        // Row 0 (top): skin, shirt, pants, hair. Row 1: shoe, white, white, white.
        (byte r, byte g, byte b)[] swatches =
        [
            (201, 149, 108), // skin
            (0, 170, 170),   // shirt (Steve cyan)
            (58, 58, 210),   // pants (Steve blue)
            (74, 47, 24),    // hair
            (90, 90, 90),    // shoe
            (255, 255, 255),
            (255, 255, 255),
            (255, 255, 255),
        ];
        var pixels = new byte[PaletteCols * PaletteRows * 4];
        for (var i = 0; i < swatches.Length; i++)
        {
            pixels[i * 4 + 0] = swatches[i].r;
            pixels[i * 4 + 1] = swatches[i].g;
            pixels[i * 4 + 2] = swatches[i].b;
            pixels[i * 4 + 3] = 255;
        }
        return pixels;
    }

    /// <summary>UV centre of a palette swatch (v = 0 is the top row, like TextureLoader).</summary>
    public static Vector2 SwatchUv(int swatch) =>
        new(((swatch % PaletteCols) + 0.5f) / PaletteCols, ((swatch / PaletteCols) + 0.5f) / PaletteRows);

    // Face order is Chunk.CubeFaces: -Z (front, the face the model looks through), +Z, -X,
    // +X, +Y, -Y. Head wears hair everywhere but the face; the torso's collar and waistband
    // peek skin/pants; forearms are bare skin under shirt sleeves; shins end in shoes.
    private static readonly int[] HeadSwatches = [Skin, Hair, Hair, Hair, Hair, Skin];
    private static readonly int[] TorsoSwatches = [Shirt, Shirt, Shirt, Shirt, Skin, Pants];
    private static readonly int[] UpperArmSwatches = [Shirt, Shirt, Shirt, Shirt, Shirt, Shirt];
    private static readonly int[] ForearmSwatches = [Skin, Skin, Skin, Skin, Shirt, Skin];
    private static readonly int[] ThighSwatches = [Pants, Pants, Pants, Pants, Pants, Pants];
    private static readonly int[] ShinSwatches = [Pants, Pants, Pants, Pants, Pants, Shoe];

    /// <summary>
    /// One mesh per rig part: every present face of every cuboid in the shared
    /// <see cref="Vertex"/> layout, textured from the rig's skin. The vertex colour stays
    /// white; asymmetric sculpts get their own left/right meshes automatically.
    /// </summary>
    public static Dictionary<string, Mesh> BuildMeshes(GL gl, PlayerRig rig)
    {
        var meshes = new Dictionary<string, Mesh>(StringComparer.Ordinal);
        foreach (var (name, part) in rig.Parts)
            meshes[name] = BuildPartMesh(gl, part);
        return meshes;
    }

    private static Mesh BuildPartMesh(GL gl, PlayerRig.RigPart part)
    {
        var vertices = new List<Vertex>();
        var indices = new List<ushort>();
        Span<Vector2> corners = stackalloc Vector2[4];

        foreach (var cube in part.Cubes)
        {
            var size = cube.Max - cube.Min;
            for (var face = 0; face < 6; face++)
            {
                // A face omitted in the file leaves a hole (open reinforced edges, no cap).
                if (cube.Faces[face] is not { } rect) continue;
                rect.CornersTo(corners);

                var positions = Chunk.CubeFaces[face].face;
                var normal = Chunk.CubeFaces[face].normal;
                var quadBase = (ushort)vertices.Count;
                for (var corner = 0; corner < 4; corner++)
                {
                    var p = positions[corner];
                    vertices.Add(new Vertex(
                        cube.Min + new Vector3(p.X * size.X, p.Y * size.Y, p.Z * size.Z),
                        255, 255, 255, 255, corners[corner], 0, 1f, 0f, normal));
                }
                indices.AddRange([(ushort)quadBase, (ushort)(quadBase + 1), (ushort)(quadBase + 2),
                    quadBase, (ushort)(quadBase + 2), (ushort)(quadBase + 3)]);
            }
        }

        return new Mesh(gl,
            CollectionsMarshal.AsSpan(vertices),
            CollectionsMarshal.AsSpan(indices));
    }
}
