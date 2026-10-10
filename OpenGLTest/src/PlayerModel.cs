using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using static silkgltriangle.PlayerRig;
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
    private const float PunchStrikeEnd = 0.55f;
    // How far the torso leans into the thrust.
    private const float PunchLean = 0.07f;
    // The off hand swings back as the fist drives out, and the shoulders twist into the blow.
    private const float PunchLeftCounter = 0.45f;
    private const float PunchTwist = 0.16f;

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
    private float _mineSpeed = 1f;
    private float _mineWeight;
    private float _sliceT = 1f;
    private int _sliceDir;
    private float _throwT = 1f;
    private float? _throwCharge01;
    private float _throwReleaseCharge;
    private float _time;
    private float _holdBlend;

    // Secondary motion. Springs carry their own velocity so a landing, a hit or an item swap rings
    // out instead of easing: the landing dip (positive = body lowered), the hit flinch (positive =
    // thrown backward), the first-person arm lag behind the look and the equip lowering.
    private Spring _landDip = new(3.2f, 0.4f);
    private Spring _flinch = new(5f, 0.3f);
    private Spring _swayYaw = new(3.5f, 0.5f);
    private Spring _swayPitch = new(3.5f, 0.5f);
    private Spring _equip = new(3.2f, 0.55f);
    private float _crouchBlend;
    private float _swimBlend;
    private float _swimPhase;
    private float _lookYaw;
    private float _lookPitch;
    private float _bodyYaw;
    private bool _lookKnown;
    private bool _wasAirborne;

    // How far the body may trail the look before it is dragged along, in degrees.
    private const float BodyYawDeadZone = 40f;
    private const float PelvisFallback = 0.75f;

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

    /// <summary>Smoothed 0..1 crouch amount (<c>sneaking</c> in <see cref="Update"/>).</summary>
    public float CrouchBlend => _crouchBlend;

    /// <summary>Smoothed 0..1 swimming amount (<c>swimming</c> in <see cref="Update"/>).</summary>
    public float SwimBlend => _swimBlend;

    /// <summary>The landing spring's displacement: positive while the body is squatting from an impact.</summary>
    public float LandingDip => _landDip.Value;

    /// <summary>The hit flinch spring: positive while the torso is thrown back, negative while it is thrown forward.</summary>
    public float Flinch => _flinch.Value;

    /// <summary>The equip spring: 1 right after <see cref="NotifyEquip"/> (arm lowered) settling to 0.</summary>
    public float EquipLowering => _equip.Value;

    /// <summary>0..1 how far the body has fallen over in a death (set by the world while it plays).</summary>
    public float DeathFall { get; set; }

    /// <summary>
    /// When true the body trails the look: it only turns once the look is more than a dead zone away
    /// and then catches up, and the head turns on its own meanwhile. Off (the body always faces the
    /// look) for the first-person body, whose fist and held item are aimed along the view.
    /// </summary>
    public bool BodyYawLags { get; set; }

    /// <summary>The yaw (degrees, the camera's convention) the body is drawn facing.</summary>
    public float BodyYaw => BodyYawLags && _lookKnown ? _bodyYaw : _lookYaw;

    /// <summary>
    /// Reports the current look. The change since the last call lags the arms behind the turn (the
    /// viewmodel sway) and, with <see cref="BodyYawLags"/>, drives the body-yaw follow. Call once a
    /// frame before <see cref="Update"/>.
    /// </summary>
    public void SetLook(float yawDeg, float pitchDeg)
    {
        if (_lookKnown)
        {
            var dYaw = WrapDegrees(yawDeg - _lookYaw);
            var dPitch = pitchDeg - _lookPitch;
            // The arms trail the view: turn right and the arm swings left; look up and it sinks.
            _swayYaw.Value = Math.Clamp(_swayYaw.Value + MathUtils.ToRadians(dYaw) * 0.55f, -0.4f, 0.4f);
            _swayPitch.Value = Math.Clamp(_swayPitch.Value - MathUtils.ToRadians(dPitch) * 0.55f, -0.4f, 0.4f);
        }
        else
        {
            _bodyYaw = yawDeg;
        }
        _lookYaw = yawDeg;
        _lookPitch = pitchDeg;
        _lookKnown = true;
    }

    /// <summary>A different item was just selected: the hand dips and springs back up with it.</summary>
    public void NotifyEquip() => _equip.Value = 1f;

    /// <summary>
    /// The body took a hit. <paramref name="fromFront"/> is +1 for a blow from the front (the torso is
    /// thrown back), -1 from behind (thrown forward), 0 for a hit with no direction (a fall, lava),
    /// which just rocks it back a little.
    /// </summary>
    public void Hurt(float fromFront = 0f)
    {
        var direction = fromFront == 0f ? 0.5f : Math.Clamp(fromFront, -1f, 1f);
        _flinch.Impulse(5.5f * direction);
    }

    private static float WrapDegrees(float degrees) => degrees - MathF.Round(degrees / 360f) * 360f;

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
        float verticalVelocity = 0f, bool sneaking = false, bool swimming = false)
    {
        if (deltaTime <= 0f) return;
        _time += deltaTime;

        // A landing hits the dip spring in proportion to the fall speed the frame before.
        var airborne = !grounded;
        if (_wasAirborne && !airborne && _airVelocity < -2.5f)
            _landDip.Impulse(MathF.Min(-_airVelocity, 20f));
        _wasAirborne = airborne;
        _landDip.Step(0f, deltaTime);
        _flinch.Step(0f, deltaTime);
        _swayYaw.Step(0f, deltaTime);
        _swayPitch.Step(0f, deltaTime);
        _equip.Step(0f, deltaTime);
        _crouchBlend = MathUtils.Damp(_crouchBlend, sneaking ? 1f : 0f, 12f, deltaTime);
        _swimBlend = MathUtils.Damp(_swimBlend, swimming ? 1f : 0f, 6f, deltaTime);
        _swimPhase += deltaTime * (2.6f + 5f * MathUtils.Clamp01(horizontalSpeed / WalkSpeedRef));

        if (BodyYawLags && _lookKnown)
        {
            var offset = WrapDegrees(_lookYaw - _bodyYaw);
            // Outside the dead zone the body is dragged along; inside it only drifts after a moving
            // player, quicker the faster they go.
            if (MathF.Abs(offset) > BodyYawDeadZone)
                _bodyYaw = _lookYaw - MathF.Sign(offset) * BodyYawDeadZone;
            var follow = MathUtils.Lerp(1.2f, 9f, MathUtils.Clamp01(horizontalSpeed / 2f));
            _bodyYaw += WrapDegrees(_lookYaw - _bodyYaw) * (1f - MathF.Exp(-follow * deltaTime));
        }

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
            _mineT = Math.Min(1f, _mineT + deltaTime * _mineSpeed / MineDuration);

        if (_sliceT < 1f)
            _sliceT = Math.Min(1f, _sliceT + deltaTime / SliceDuration);

        if (_throwT < 1f)
            _throwT = Math.Min(1f, _throwT + deltaTime / ThrowDuration);
    }

    /// <summary>
    /// Starts one hand swing (a mining hit just landed). <paramref name="weight"/> is 0 for a light
    /// swing (bare hand, a quick tool) up to 1 for a heavy one (a slow tool): a heavy swing takes
    /// longer and leans the body harder into the blow.
    /// </summary>
    public void Punch(float weight = 0f)
    {
        _mineWeight = MathUtils.Clamp01(weight);
        _mineSpeed = MathUtils.Lerp(1f, 0.7f, _mineWeight);
        _mineT = 0f;
    }

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

    /// <param name="pitchDeg">The camera's look pitch, which the jab is thrown along and nothing
    /// else in the pose is: the hips, knees and torso keep facing the body's own way.</param>
    public Pose GetPose(float pitchDeg = 0f)
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

        var bobAmp = MathUtils.Lerp(WalkBobAmp, RunBobAmp, _runBlend);
        var bob = MathF.Abs(MathF.Cos(_walkPhase)) * bobAmp * _moveBlend;
        var torsoPitch = -_moveBlend * MathUtils.Lerp(WalkTorsoPitch, RunTorsoPitch, _runBlend);
        var ground = new Pose(swing, -swing, leftKnee, rightKnee,
            leftShoulder, rightShoulder, leftElbow, rightElbow, torsoPitch, bob);

        // The punch rides on whichever pose the frame is in (ground cycle or jump), applied
        // after the two are blended so a swing still plays while running or falling. The swim
        // stroke and the body mods (landing, crouch, flinch, sway) sit between the two.
        if (_airBlend <= 0.001f)
            return WithThrow(WithPunch(ApplyMods(ApplySwim(ground), ComputeMods()), pitchDeg));

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
        return WithThrow(WithPunch(ApplyMods(ApplySwim(blended), ComputeMods()), pitchDeg));
    }

    // Swimming replaces the ground cycle: arms rock through an overhead crawl stroke opposite each
    // other (a gentle sculling when still), the legs flutter, the knees stay soft. Blended in by
    // the smoothed swim amount, so entering and leaving the water never snaps.
    private Pose ApplySwim(in Pose pose)
    {
        var s = _swimBlend;
        if (s <= 0.001f) return pose;
        var crawl = MathUtils.Clamp01(_moveBlend * 3f);
        var stroke = MathF.Sin(_swimPhase);
        var arm = 1.2f + 0.1f * MathF.Sin(_swimPhase * 0.5f);
        var leftShoulder = arm + 1.5f * crawl * stroke;
        var rightShoulder = arm - 1.5f * crawl * stroke + _holdBlend * HoldShoulder * 0.5f;
        var leftElbow = 0.4f + 0.55f * crawl * MathF.Max(0f, MathF.Cos(_swimPhase));
        var rightElbow = 0.4f + 0.55f * crawl * MathF.Max(0f, -MathF.Cos(_swimPhase));
        var kick = MathF.Sin(_swimPhase * 2.4f) * (0.2f + 0.2f * crawl);
        var bob = MathF.Sin(_swimPhase * 0.7f) * 0.025f;
        return new Pose(
            MathUtils.Lerp(pose.LeftHip, kick, s), MathUtils.Lerp(pose.RightHip, -kick, s),
            MathUtils.Lerp(pose.LeftKnee, -0.35f - MathF.Max(0f, kick), s),
            MathUtils.Lerp(pose.RightKnee, -0.35f - MathF.Max(0f, -kick), s),
            MathUtils.Lerp(pose.LeftShoulder, leftShoulder, s), MathUtils.Lerp(pose.RightShoulder, rightShoulder, s),
            MathUtils.Lerp(pose.LeftElbow, leftElbow, s), MathUtils.Lerp(pose.RightElbow, rightElbow, s),
            MathUtils.Lerp(pose.TorsoPitch, -0.15f * crawl, s), MathUtils.Lerp(pose.Bob, bob, s));
    }

    /// <summary>The body-wide offsets the second pass adds on top of the pose (see <see cref="ComputeMods"/>).</summary>
    private readonly struct Mods
    {
        public float HipBend { get; init; }
        public float KneeBend { get; init; }
        public float Shoulders { get; init; }
        public float RightShoulderExtra { get; init; }
        public float TorsoPitch { get; init; }
        public float Drop { get; init; }
        public float TorsoYaw { get; init; }
        public float TorsoRoll { get; init; }
        public float HeadPitch { get; init; }
        public float HeadYaw { get; init; }
        public float SwimPitch { get; init; }
        public float DeathPitch { get; init; }
    }

    private Mods ComputeMods()
    {
        var dip = Math.Clamp(_landDip.Value, -0.15f, 0.6f);
        var crouch = _crouchBlend;
        var ground = 1f - _airBlend;
        var move = _moveBlend * ground * (1f - _swimBlend);

        // Slice: the shoulders twist with the sweep and the body shifts its weight onto the lead leg.
        var sliceTwist = IsSlicing ? SliceSweep() * 0.2f : 0f;
        var sliceRoll = IsSlicing ? _sliceDir * 0.07f * SliceStroke() : 0f;

        // Throw: lean back while the arm is cocked, then fall forward through the release.
        float throwLean = 0f;
        if (_throwCharge01.HasValue)
            throwLean = 0.18f * MathUtils.SmoothStep(_throwCharge01.Value);
        else if (_throwT < 1f)
        {
            var cocked = 0.18f * MathUtils.SmoothStep(_throwReleaseCharge);
            var strike = MathUtils.EaseOutCubic(MathF.Min(1f, _throwT / ThrowStrikeEnd));
            throwLean = MathUtils.Lerp(cocked, -0.3f, strike) * (1f - MathUtils.SmoothStep((_throwT - ThrowStrikeEnd) / (1f - ThrowStrikeEnd)));
        }

        var fall = DeathFall;
        return new Mods
        {
            HipBend = dip * 0.55f + crouch * 0.7f,
            KneeBend = dip * 1.1f + crouch * 1.4f,
            Shoulders = _swayPitch.Value + dip * 0.3f,
            RightShoulderExtra = -_equip.Value * 0.9f,
            TorsoPitch = -dip * 0.2f - crouch * 0.25f + _flinch.Value * 0.3f + throwLean,
            Drop = dip * 0.12f + crouch * 0.17f,
            TorsoYaw = MathF.Sin(_walkPhase) * 0.1f * move + sliceTwist
                       - PunchTwist * PunchWeights(_mineT).Extend,
            TorsoRoll = MathF.Cos(_walkPhase) * 0.035f * move + sliceRoll + _flinch.Value * 0.08f,
            HeadPitch = _flinch.Value * 0.45f - crouch * 0.1f,
            HeadYaw = BodyYawLags && _lookKnown ? -MathUtils.ToRadians(WrapDegrees(_lookYaw - _bodyYaw)) : 0f,
            SwimPitch = -_swimBlend * MathUtils.Clamp01(_moveBlend * 3f) * 1.1f,
            DeathPitch = MathUtils.EaseInCubic(MathUtils.Clamp01(fall)) * 1.5f,
        };
    }

    private static Pose ApplyMods(in Pose pose, in Mods m) => new(
        pose.LeftHip + m.HipBend, pose.RightHip + m.HipBend,
        pose.LeftKnee - m.KneeBend, pose.RightKnee - m.KneeBend,
        pose.LeftShoulder + m.Shoulders, pose.RightShoulder + m.Shoulders + m.RightShoulderExtra,
        pose.LeftElbow, pose.RightElbow,
        pose.TorsoPitch + m.TorsoPitch, pose.Bob - m.Drop);

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

    // The jab's eased phase weights, all zero at either end of the swing so the pose never jumps
    // when it starts, crosses the impact or finishes. Retract draws the fist up to the aim line
    // over the wind-up, Extend drives it out to full extension across the thrust and eases back
    // out over the recovery, and Recover settles the chambered pose back onto the base one. The
    // torso lean and the inward arm yaw both ride Extend, so they follow the fist out and back.
    private static (float Retract, float Extend, float Recover) PunchWeights(float p)
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

    // A fist position in the view frame as an offset from the shoulder in body space. The
    // shoulder does not rotate with the look — it sits ShoulderX to the side, ShoulderY above
    // the feet and FirstPersonBodyBack behind the eye — so the view-frame point has to be turned
    // back out by the pitch before the arm can be solved to it. Y is the up axis and Z points
    // forward out of the eye, so a point in front has a negative Z here.
    private static Vector3 PunchTargetFromShoulder(Vector3 viewFist, float pitchRad)
    {
        var cos = MathF.Cos(pitchRad);
        var sin = MathF.Sin(pitchRad);
        return new Vector3(
            viewFist.X - ShoulderX,
            viewFist.Y * cos + viewFist.Z * sin - (ShoulderY - Camera.EyeHeight),
            viewFist.Y * sin - viewFist.Z * cos - FirstPersonBodyBack);
    }

    // Shoulder and elbow that put the fist on a view-frame point, for a camera pitched by
    // pitchRad. Beyond the arm's reach the solver pins the elbow straight, which leaves the jab
    // pointing at the target and merely falling short of it.
    private static (float Shoulder, float Elbow) PunchTargetJoints(Vector3 viewFist, float pitchRad)
    {
        var target = PunchTargetFromShoulder(viewFist, pitchRad);
        var lateral = MathF.Sqrt(target.X * target.X + target.Z * target.Z);
        return SolveArm(target.Y, -lateral);
    }

    // Azimuth of the fist on a view-frame point: the arm yaws to swing across to the aim's own
    // side of the shoulder. Absolute, so the caller subtracts the fixed ArmInwardYaw that
    // UpperArm adds by itself.
    private static float PunchTargetYaw(Vector3 viewFist, float pitchRad)
    {
        var target = PunchTargetFromShoulder(viewFist, pitchRad);
        return MathF.Atan2(-target.X, -target.Z);
    }

    // Right shoulder and elbow through the jab: chamber the fist against the aim line, drive it
    // out to full extension (the elbow is the joint that actually does the punching), then ease
    // back to the base pose.
    private static (float Shoulder, float Elbow) PunchJoints(in Pose pose, float p, float pitchRad)
    {
        var w = PunchWeights(p);
        var (chamberShoulder, chamberElbow) = PunchTargetJoints(PunchChamberFist, pitchRad);
        var (extendShoulder, extendElbow) = PunchTargetJoints(PunchExtendFist, pitchRad);
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
    private float PunchActivity()
    {
        if (_mineT >= 1f)
            return 0f;
        var w = PunchWeights(_mineT);
        return MathF.Max(w.Retract, w.Extend);
    }

    // The jab's own arm yaw in radians, absolute in body space, riding the same weights as the
    // joints. Depends only on the swing timer and the pitch, so GetPartMatrix and the hand anchor
    // can both ask for it without re-deriving the pose.
    private float PunchArmYaw(float pitchRad)
    {
        if (_mineT >= 1f)
            return 0f;
        var w = PunchWeights(_mineT);
        var yaw = MathUtils.Lerp(0f, PunchTargetYaw(PunchChamberFist, pitchRad), w.Retract);
        yaw = MathUtils.Lerp(yaw, PunchTargetYaw(PunchExtendFist, pitchRad), w.Extend);
        return MathUtils.Lerp(yaw, 0f, w.Recover);
    }

    private Pose WithPunch(in Pose pose, float pitchDeg)
    {
        if (_mineT >= 1f)
            return pose;
        var pitchRad = MathUtils.ToRadians(pitchDeg);
        var (shoulder, elbow) = PunchJoints(pose, _mineT, pitchRad);
        return new Pose(pose.LeftHip, pose.RightHip, pose.LeftKnee, pose.RightKnee,
            pose.LeftShoulder - PunchLeftCounter * PunchWeights(_mineT).Extend, shoulder, pose.LeftElbow, elbow,
            pose.TorsoPitch - PunchLean * (1f + _mineWeight * 1.5f) * PunchWeights(_mineT).Extend, pose.Bob);
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
        var pose = GetPose(pitchDeg);
        var pitchRad = MathUtils.ToRadians(pitchDeg);
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
        // A lagging body faces its own yaw and the head makes up the difference.
        var mods = ComputeMods();
        var bodyYawDeg = BodyYawLags && _lookKnown ? _bodyYaw : yawDeg;
        var yawRad = -(bodyYawDeg + 90f) * MathF.PI / 180f;
        // Swimming tips the body forward about the hips, a death lays it over about the feet.
        var pelvisY = rig.Parts.TryGetValue("ThighL", out var thigh) ? thigh.Pivot.Y : PelvisFallback;
        var local = Matrix4x4.Identity;
        if (mods.SwimPitch != 0f)
            local = Matrix4x4.CreateTranslation(0f, -pelvisY, 0f) * Matrix4x4.CreateRotationX(mods.SwimPitch)
                    * Matrix4x4.CreateTranslation(0f, pelvisY, 0f);
        if (mods.DeathPitch != 0f)
            local *= Matrix4x4.CreateRotationX(mods.DeathPitch);
        var root = local * Matrix4x4.CreateRotationY(yawRad) * Matrix4x4.CreateTranslation(feet + new Vector3(0f, pose.Bob, 0f));
        var swingAxis = Vector3.UnitX;

        Matrix4x4 Torso() =>
            Matrix4x4.CreateFromAxisAngle(swingAxis, pose.TorsoPitch)
            * Matrix4x4.CreateRotationY(mods.TorsoYaw) * Matrix4x4.CreateRotationZ(mods.TorsoRoll)
            * Matrix4x4.CreateTranslation(Pivot("Torso")) * root;

        Matrix4x4 Head() =>
            Matrix4x4.CreateFromAxisAngle(swingAxis, pitchDeg * MathF.PI / 180f * 0.8f + mods.HeadPitch)
            * Matrix4x4.CreateRotationY(mods.HeadYaw)
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
                * Matrix4x4.CreateFromAxisAngle(Vector3.UnitY, inward + sweep + _swayYaw.Value)
                * Matrix4x4.CreateTranslation(pivot) * root;
        }

        Matrix4x4 Thigh(Vector3 pivot, float hip) =>
            Matrix4x4.CreateFromAxisAngle(swingAxis, hip)
            * Matrix4x4.CreateTranslation(pivot) * root;

        var sliceSweep = SliceSweep();
        // The jab swings the arm across to the aim's own side as it extends, and back out on the
        // recovery. PunchArmYaw is absolute while UpperArm adds ArmInwardYaw on its own, so that
        // fixed term is taken back out here.
        var rightArmYaw = sliceSweep + PunchArmYaw(pitchRad) - ArmInwardYaw;
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
            "UpperArmR" => UpperArm(Pivot("UpperArmR"), rightShoulder, rightArmYaw),
            "ForearmL" => Matrix4x4.CreateFromAxisAngle(swingAxis, pose.LeftElbow)
                * Matrix4x4.CreateTranslation(Pivot("ForearmL") - Pivot("UpperArmL"))
                * UpperArm(Pivot("UpperArmL"), pose.LeftShoulder, 0f),
            "ForearmR" => Matrix4x4.CreateFromAxisAngle(swingAxis, rightElbow)
                * Matrix4x4.CreateTranslation(Pivot("ForearmR") - Pivot("UpperArmR"))
                * UpperArm(Pivot("UpperArmR"), rightShoulder, rightArmYaw),
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
    /// and the punch, but oriented in body space. Parenting the orientation to the
    /// forearm would force absurd display angles the moment the carrying pose raises the
    /// arm (a level tool would need -130 degrees of pitch), so the JSON numbers stay human.
    /// <paramref name="pitchDeg"/> is the camera look the jab is thrown along; it must match the
    /// pitch the body was drawn with or the tool misses the fist it is supposed to sit in.
    /// </summary>
    public Matrix4x4 GetHandAnchorMatrix(Vector3 eyePos, float yawDeg, float backOffset = 0f,
        float pitchDeg = 0f)
    {
        var fist = Vector3.Transform(new Vector3(0f, HandDepth(), 0f),
            GetPartMatrix("ForearmR", eyePos, yawDeg, pitchDeg, backOffset));
        var yawRad = -((BodyYawLags && _lookKnown ? _bodyYaw : yawDeg) + 90f) * MathF.PI / 180f;
        var pitchRad = MathUtils.ToRadians(pitchDeg);
        // The slice sweep and blade roll ride the anchor as well as the fist position, so the held
        // blade turns through the arc and rolls from upright to horizontal instead of only sliding
        // along it. The roll is applied first (model space), then the body yaw and sweep, so its
        // axis stays fixed to the body.
        //
        // The jab is thrown along the aim rather than the body, so the tool takes the look pitch
        // and the jab's swing for the length of the swing, then drops both as the arm settles.
        var punch = PunchActivity();
        return Matrix4x4.CreateRotationZ(SliceRoll())
            * Matrix4x4.CreateRotationX(pitchRad * punch)
            * Matrix4x4.CreateRotationY(yawRad + SliceSweep() + PunchArmYaw(pitchRad) + _swayYaw.Value)
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

}
