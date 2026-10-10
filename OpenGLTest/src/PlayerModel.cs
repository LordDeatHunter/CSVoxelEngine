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
    private const float JumpHipFront = 0.75f;
    private const float JumpHipBack = 0.30f;
    private const float SprintJumpHipFront = 0.95f;
    private const float SprintJumpHipBack = -0.30f;
    private const float JumpKneeFront = -1.6f;
    private const float JumpKneeBack = -1.3f;
    private const float SprintJumpKneeFront = -1.0f;
    private const float SprintJumpKneeBack = -1.8f;

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
    // The chambered punch: the upper arm flares out to the side and forward and the forearm is folded
    // across so the wrist points to the front; the thrust then brings the elbow in and forward.
    private const float PunchChamberRoll = 1.2f;
    private const float PunchChamberYaw = 0.35f;
    private const float PunchChamberForearm = 1.15f;

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
    private const float SliceWindupRadians = 1.0f;
    private const float SliceSweepRadians = 0.8f;
    // The slicing arm is held out in front, nearly straight, at about chest height: the upper arm raised to
    // roughly horizontal (SliceArmShoulder, a flexion about X) with only a slight bend at the elbow
    // (SliceArmElbow), so the forearm points forward along the arm rather than up. The carrying pose (elbow bent
    // 75 degrees, forearm up) is eased into this as the stroke ramps in, and back out as it ramps out.
    private const float SliceArmShoulder = 1.5f;
    private const float SliceArmElbow = 0.3f;
    // Wind-up (0..WindupEnd) reaches the start side; the strike (..StrikeEnd) crosses to the far
    // side; the rest is the recovery back to the idle pose.
    //
    // The phases are uneven on purpose, with the same total duration: a slower wind-up that coils the
    // body (to 0.26), a short, sharp strike that is centred on the impact (0.26..0.49, about twice as
    // fast as before) and a long, eased recovery, so the contrast reads as a snap.
    private const float SliceWindupEnd = 0.26f;
    private const float SliceStrikeEnd = 0.49f;

    // The slice is a whole-body motion: the arm sweep is only part of it. The entire body turns about
    // the feet (SliceBodyYaw, the legs counter-turning so the feet stay planted), the shoulders turn
    // further (SliceTorsoYaw), the weight drops into the strike, and the hand travels a small
    // diagonal from well above the chest line (SliceStartRaiseBlocks) down to just below it (SliceEndDropBlocks) on every slice,
    // whichever way it swings.
    private const float SliceBodyYaw = 0.42f;
    private const float SliceTorsoYaw = 0.5f;
    private const float SliceLegCounter = 0.6f;
    private const float SliceStartRaiseBlocks = 0.28f;
    private const float SliceEndDropBlocks = 0.10f;
    // The blade is carried by the wrist: its tip points along the forearm (so it follows the sweep of the arm and
    // is always across the direction the hand travels, never turned up), rolled about its own length by
    // SliceBladeRoll (an edge-leading cant, not laid fully flat) and cocked up from the forearm by SliceBladeCant,
    // the way a blade is held out ahead of the hand. Both mirror with the swing's direction.
    private const float SliceBladeRoll = 0.8f;
    private const float SliceBladeCant = 0.12f;
    // During the main part of the cut (SliceMainWeight) the blade is also turned about the vertical by this much
    // from the forearm's direction, in the engine's yaw convention (positive: counter-clockwise seen from above, toward the
    // player's left for the first swing, so the tip leads ahead of the hand's travel). Mirrored for the return.
    private const float SliceBladeYawOffset = 0.785f;
    // On top of that, through the main part of the cut the held blade is drawn with this much added to its own display
    // rotation (HeldItemRotationOffset): the same [pitch, yaw, roll] degrees the item JSON's display.rotation and the item
    // debugger's Rot sliders use, so a value found by sliding them at a mid-cut frame transfers here as is. The blade's
    // rest display is yaw -90 / roll 45; mid-cut it reads right at yaw -145 / roll 70. The yaw part mirrors for the
    // return swing (for a display yaw of -90 the mirror image of yaw -90 + d is yaw -90 - d), the roll does not.
    private const float SliceBladeDisplayYawDegrees = -55f;
    private const float SliceBladeDisplayRollDegrees = 25f;
    private const float SliceBodyWindup = 1.0f;
    private const float SliceBodyFar = 0.85f;

    // Charged throw (right-click in the air with a throwable). While held, the right arm
    // rises forward-up overhead into a cocked pre-throw with the elbow extending on the
    // way up, eased by the charge amount so the raise reads as slow. The cock goes the
    // forward way (never backward): swinging back would drag the hand down past the leg
    // and leave it hanging behind the back. Absolute joint targets (not deltas) so the
    // pose is the same held or empty-handed. On release the arm strikes fast from the
    // exact release pose through a follow-through, then settles slowly back to rest —
    // one direction change, which reads as follow-through rather than a bounce.
    public const float ThrowDuration = 0.55f;
    private const float ThrowCockedShoulder = 1.3f;
    private const float ThrowCockedElbow = 0.6f;
    // The cocked pose proper (ApplyArmFrames): the upper arm out to the right and a little behind
    // the body, the forearm bent up at about a right angle to it, wrist above the elbow, and the
    // shoulders wound back toward the throwing side. These replace the old straight-up arm.
    private const float ThrowArmRoll = 1.4f;
    private const float ThrowArmFlex = 0.1f;
    private const float ThrowArmBehind = 0.4f;
    private const float ThrowForearmTilt = 0.3f;
    private const float ThrowForearmUp = 1.45f;
    private const float ThrowWindTwist = 0.55f;
    private const float ThrowFollowTwist = 0.3f;
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
    // The shoulders' rubber-band: each shoulder joint is pulled a little toward where its arm is reaching
    // (see PullTarget), through a spring so a sudden thrust or release trails and settles. x, y, z in torso space.
    private readonly Spring[] _pullR = [new(5f, 0.7f), new(5f, 0.7f), new(5f, 0.7f)];
    private readonly Spring[] _pullL = [new(5f, 0.7f), new(5f, 0.7f), new(5f, 0.7f)];
    private const float MaxShoulderPull = 0.08f;
    // The shoulder joint sits on the torso's side face (the arm's pivot is ShoulderX - the torso's half width = 0.1
    // outside it, so a hanging arm is flush). Swing the arm up and across and it would hinge on that outer point with a
    // gap opening against the body, so the joint slides in toward the torso as the arm lifts off hanging
    // (SocketInward at about 70 degrees), keeping the top of the arm seated on the body.
    private const float ShoulderSocketInward = 0.09f;
    /// <summary>The farthest a shoulder is ever pulled off its rest place on the torso, in blocks.</summary>
    public const float MaxShoulderPullBlocks = MaxShoulderPull;
    private float _crouchBlend;
    private float _swimBlend;
    // Seconds since the last swim stroke began; the stroke is over (the arms rest in front) once it
    // reaches PlayerPhysics.SwimStrokeSeconds, and the first stroke after entering the water is ready.
    private float _swimT = PlayerPhysics.SwimStrokeSeconds;
    private float _swimFwd;
    private float _swimSide;
    private float _swimBobTime;
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

    /// <summary>
    /// Degrees the pose adds to the held item's own display rotation, in the item JSON's <c>[pitch, yaw, roll]</c>
    /// order (what <c>Item.DisplayMatrix</c> and the item debugger's Rot sliders take), so a value found by sliding
    /// them at a frame of an animation transfers here unchanged. Zero at rest; through the main part of a slice it is
    /// the blade's mid-cut turn (<see cref="SliceBladeDisplayYawDegrees"/>, <see cref="SliceBladeDisplayRollDegrees"/>),
    /// the yaw mirrored for the return swing.
    /// </summary>
    public Vector3 HeldItemRotationOffset => IsSlicing
        ? new Vector3(0f, SliceBladeDisplayYawDegrees * _sliceDir, SliceBladeDisplayRollDegrees) * SliceMainWeight()
        : Vector3.Zero;

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

    /// <summary>
    /// How far through the current swim stroke the arms are, 0 at the thrust out to the sides and 1
    /// once they have crept back to the front (also while no stroke is under way).
    /// </summary>
    public float SwimStrokeProgress => MathUtils.Clamp01(_swimT / PlayerPhysics.SwimStrokeSeconds);

    /// <summary>
    /// When true the model starts its own strokes while it swims and moves (a remote player, whose
    /// strokes are not reported); the local player is told by <see cref="TriggerSwimStroke"/> instead.
    /// </summary>
    public bool AutoSwimStroke { get; set; }

    /// <summary>A swim stroke just started (the physics kicked the swimmer): the arms thrust out from the front.</summary>
    public void TriggerSwimStroke() => _swimT = 0f;

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
        float verticalVelocity = 0f, bool sneaking = false, bool swimming = false,
        float swimForward = 0f, float swimSide = 0f)
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
        var pullR = PullTarget(right: true);
        var pullL = PullTarget(right: false);
        for (var i = 0; i < 3; i++)
        {
            _pullR[i].Step(pullR[i], deltaTime);
            _pullL[i].Step(pullL[i], deltaTime);
        }
        _crouchBlend = MathUtils.Damp(_crouchBlend, sneaking ? 1f : 0f, 12f, deltaTime);
        _swimBlend = MathUtils.Damp(_swimBlend, swimming ? 1f : 0f, 6f, deltaTime);
        _swimBobTime += deltaTime;
        _swimT += deltaTime;
        if (swimming && AutoSwimStroke && _swimT >= PlayerPhysics.SwimStrokeSeconds && horizontalSpeed > 0.8f)
            _swimT = 0f;
        // The body tips toward where the swimmer is heading, so these follow the velocity.
        _swimFwd = MathUtils.Damp(_swimFwd, swimming ? swimForward : 0f, 8f, deltaTime);
        _swimSide = MathUtils.Damp(_swimSide, swimming ? swimSide : 0f, 8f, deltaTime);

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
    private float SliceSweep() => SlicePhase(SliceWindupRadians, SliceSweepRadians);

    // The whole body's turn through the same phases, normalised: about 1 at the wind-up (toward the
    // swing's start side) and -0.85 at the follow-through, with the sign of the swing's direction.
    private float SliceBodySweep() => SlicePhase(SliceBodyWindup, SliceBodyFar);

    private float SlicePhase(float windupAmplitude, float farAmplitude)
    {
        var p = MathUtils.Clamp01(_sliceT);
        if (p <= 0f || p >= 1f) return 0f;

        var startSide = -windupAmplitude * _sliceDir;
        var farSide = farAmplitude * _sliceDir;
        if (p < SliceWindupEnd)
            return MathUtils.Lerp(0f, startSide, MathUtils.SmoothStep(p / SliceWindupEnd));
        // The strike accelerates hard through the middle (a steeper curve than the other phases).
        if (p < SliceStrikeEnd)
            return MathUtils.Lerp(startSide, farSide,
                MathUtils.EaseInOutCubic((p - SliceWindupEnd) / (SliceStrikeEnd - SliceWindupEnd)));
        return MathUtils.Lerp(farSide, 0f,
            MathUtils.SmoothStep((p - SliceStrikeEnd) / (1f - SliceStrikeEnd)));
    }

    // 0 -> 1 across the wind-up, 1 through the strike, back to 0 over the recovery: it drives the arm
    // lift and the blade roll, which are raised before the strike starts and held through it. It is 0
    // at both ends, so the swing never jumps when it starts or finishes.
    private float SliceStroke()
    {
        var p = MathUtils.Clamp01(_sliceT);
        if (p <= 0f || p >= 1f) return 0f;
        if (p < SliceWindupEnd) return MathUtils.SmoothStep(p / SliceWindupEnd);
        if (p < SliceStrikeEnd) return 1f;
        return 1f - MathUtils.SmoothStep((p - SliceStrikeEnd) / (1f - SliceStrikeEnd));
    }

    // The small diagonal: the hand rises a little in the wind-up, is carried from above the chest line
    // to below it through the strike and returns over the recovery. The same for either direction, so
    // every slice cuts from the top toward the bottom.
    private float SliceDiagonal()
    {
        var p = MathUtils.Clamp01(_sliceT);
        if (p <= 0f || p >= 1f) return 0f;
        if (p < SliceWindupEnd)
            return SliceStartRaiseBlocks * MathUtils.SmoothStep(p / SliceWindupEnd);
        if (p < SliceStrikeEnd)
            return MathUtils.Lerp(SliceStartRaiseBlocks, -SliceEndDropBlocks,
                MathUtils.EaseInOutCubic((p - SliceWindupEnd) / (SliceStrikeEnd - SliceWindupEnd)));
        return -SliceEndDropBlocks * (1f - MathUtils.SmoothStep((p - SliceStrikeEnd) / (1f - SliceStrikeEnd)));
    }

    // The hand's offset from where the extended arm puts it: the diagonal alone (it starts above that height and
    // finishes below it), through the same stroke so it is back at the idle pose by the end (no snap).
    private float SliceDrop() => SliceDiagonal();

    // The main part of the cut, as a weight: 0 until the wind-up is nearly over, full through the strike and faded out
    // again over the start of the recovery, so what rides it (the blade's turn, its display offset) comes into the cut
    // and goes back out of it, and the rest frames are untouched.
    private float SliceMainWeight()
    {
        var p = MathUtils.Clamp01(_sliceT);
        if (p <= 0f || p >= 1f) return 0f;
        if (p < SliceWindupEnd) return MathUtils.SmoothStep(0.15f, SliceWindupEnd, p);
        if (p < SliceStrikeEnd) return 1f;
        return 1f - MathUtils.SmoothStep(SliceStrikeEnd, 0.65f, p);
    }

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

    // The stroke clock as three phases: the thrust out to the sides (fast), a short hold with the
    // arms out, and the slow recovery back to the front, after which the cycle rests until the next
    // stroke. StrokeOut is 0 with the arms in front and 1 with them out to the sides.
    private const float StrokeThrustEnd = 0.2f;
    private const float StrokeHoldEnd = 0.3f;

    private float StrokeOut()
    {
        var t = SwimStrokeProgress;
        if (t >= 1f) return 0f;
        if (t < StrokeThrustEnd) return MathUtils.EaseOutCubic(t / StrokeThrustEnd);
        if (t < StrokeHoldEnd) return 1f;
        return 1f - MathUtils.SmoothStep((t - StrokeHoldEnd) / (1f - StrokeHoldEnd));
    }

    // Swimming replaces the ground cycle for the legs and body: they kick at the burst (snapping
    // from tucked to straight with the thrust), then draw back up slowly while the arms recover.
    // The arms themselves are the arm frames below. Blended in by the smoothed swim amount, so
    // entering and leaving the water never snaps.
    private Pose ApplySwim(in Pose pose)
    {
        var s = _swimBlend;
        if (s <= 0.001f) return pose;
        var t = SwimStrokeProgress;
        // 0 = legs straight (just kicked), 1 = drawn up under the body.
        float tuck;
        if (t >= 1f) tuck = 0.55f;
        else if (t < StrokeThrustEnd) tuck = MathUtils.Lerp(0.55f, 0f, MathUtils.EaseOutCubic(t / StrokeThrustEnd));
        else tuck = 0.55f * MathUtils.SmoothStep((t - StrokeThrustEnd) / (1f - StrokeThrustEnd));
        var hip = 0.1f + 0.5f * tuck;
        var knee = -(0.15f + 1.1f * tuck);
        var bob = MathF.Sin(_swimBobTime * 1.7f) * 0.025f;
        return new Pose(
            MathUtils.Lerp(pose.LeftHip, hip + 0.08f, s), MathUtils.Lerp(pose.RightHip, hip - 0.08f, s),
            MathUtils.Lerp(pose.LeftKnee, knee, s), MathUtils.Lerp(pose.RightKnee, knee, s),
            pose.LeftShoulder, pose.RightShoulder, pose.LeftElbow, pose.RightElbow,
            MathUtils.Lerp(pose.TorsoPitch, 0f, s), MathUtils.Lerp(pose.Bob, bob, s));
    }

    // How far an arm has lifted off hanging, 0..1 (smooth over about 0.15 to 1.2 radians), from its upper-arm frame.
    private static float SocketWeight(Quaternion upper)
    {
        var axis = Vector3.Transform(new Vector3(0f, -1f, 0f), upper);
        var lift = MathF.Acos(Math.Clamp(-axis.Y, -1f, 1f));
        return MathUtils.SmoothStep(0.15f, 1.2f, lift);
    }

    // ---- Shoulder pull -------------------------------------------------------------------
    // The shoulders ride on the torso (they are children of its frame), so they can never detach from it.
    // On top of that a heavily extended arm drags its shoulder a little toward where it reaches, instead of
    // the limb stretching away from a body that stays put: torso-space offsets (x right, y up, z back)
    // driven by how far each action has taken the arm out, never more than MaxShoulderPull.
    private Vector3 PullTarget(bool right)
    {
        var side = right ? 1f : -1f;
        var pull = Vector3.Zero;

        // Arms thrown out in a jump lift and widen their shoulders.
        var air = _airBlend * (1f - _swimBlend);
        pull += new Vector3(0.015f * side, 0.025f, 0f) * air;
        // The swim stroke throws both shoulders out with the arms.
        pull += new Vector3(0.025f * side, 0.015f, -0.02f) * (_swimBlend * StrokeOut());

        if (right)
        {
            // A cocked throw reaches out to the right and back and lifts the shoulder.
            pull += new Vector3(0.035f, 0.025f, 0.035f) * ThrowArmWeight();
            // A slice drags the shoulder across with the sweep.
            pull += new Vector3(-0.035f * SliceBodySweep(), 0f, 0f);
            if (_mineT < 1f)
            {
                var w = PunchWeights(_mineT);
                // The chambered elbow flares out, the thrust pushes the shoulder forward after the fist.
                pull += new Vector3(0.03f, 0f, 0f) * (w.Retract * (1f - w.Extend) * (1f - w.Recover));
                pull += new Vector3(0f, 0.01f, -0.05f) * (w.Extend * (1f - w.Recover));
            }
        }
        else if (_mineT < 1f)
        {
            // The off hand swings back as the fist drives out and takes its shoulder with it.
            pull += new Vector3(0f, 0f, 0.03f) * PunchWeights(_mineT).Extend;
        }

        var length = pull.Length();
        return length > MaxShoulderPull ? pull * (MaxShoulderPull / length) : pull;
    }

    /// <summary>
    /// Where a shoulder joint sits relative to its rest place on the torso: the spring-smoothed pull toward where the
    /// arm reaches, in torso space (x right, y up, z back), at most <see cref="MaxShoulderPull"/> long.
    /// </summary>
    public Vector3 ShoulderPull(bool right)
    {
        var springs = right ? _pullR : _pullL;
        var pull = new Vector3(springs[0].Value, springs[1].Value, springs[2].Value);
        var length = pull.Length();
        return length > MaxShoulderPull ? pull * (MaxShoulderPull / length) : pull;
    }

    // ---- Arm frames ----------------------------------------------------------------------
    // The base arm is a forward flex about X plus a yaw about the vertical: both of its bones stay in
    // one plane, so it cannot reach out to the side. Poses that need that (the airborne arms, the
    // punch's flared elbow, the throw's cock, the swim stroke) are instead given as an abduction
    // (roll about the body's front axis), a flexion about X and a yaw, and the forearm as a flexion
    // plus a bend about the roll's own axis, then slerped over the base by a weight. Roll first, then
    // flexion, then yaw (System.Numerics is row-vector, so the leftmost factor runs first): with the
    // arm held out to the side the elbow hinge (X) is the vertical axis, so a plain forearm flexion
    // swings it forward in the horizontal plane, and a bend about Z lifts it up.
    private static Quaternion FrameOf(Matrix4x4 m) => Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));

    // side is +1 for the right arm and -1 for the left, which mirrors the roll and the yaw.
    private static Quaternion UpperFrame(float side, float roll, float flex, float yaw) =>
        FrameOf(Matrix4x4.CreateRotationZ(side * roll) * Matrix4x4.CreateRotationX(flex)
                * Matrix4x4.CreateRotationY(side * yaw));

    private static Quaternion ForeFrame(float side, float flex, float bend) =>
        FrameOf(Matrix4x4.CreateRotationX(flex) * Matrix4x4.CreateRotationZ(side * bend));

    private static void Blend(ref Quaternion frame, Quaternion target, float weight)
    {
        if (weight > 0.001f) frame = Quaternion.Slerp(frame, target, MathUtils.Clamp01(weight));
    }

    /// <summary>Lays the pose-specific arm frames over the base ones; <paramref name="side"/> is +1 right, -1 left.</summary>
    private void ApplyArmFrames(float side, ref Quaternion upper, ref Quaternion fore)
    {
        var right = side > 0f;

        // Jumping: arms out to the sides. A carried item keeps the right arm nearer the front.
        var air = _airBlend * (1f - _swimBlend) * (right ? 1f - 0.6f * _holdBlend : 1f);
        if (air > 0.001f)
        {
            var rise = MathUtils.Clamp01(_airVelocity / 11f);
            var fall = MathUtils.Clamp01(-_airVelocity / 12f);
            var roll = MathUtils.Lerp(AirArmRoll, SprintAirArmRoll, _airRun) + (rise - fall * 0.3f) * 0.25f;
            Blend(ref upper, UpperFrame(side, roll, MathUtils.Lerp(AirArmFlex, SprintAirArmFlex, _airRun), 0f), air);
            Blend(ref fore, ForeFrame(side, MathUtils.Lerp(AirElbowFlex, SprintAirElbowFlex, _airRun), 0f), air);
        }

        // Swimming: the arms reach out in front, thrust out to the sides at the burst (a little behind
        // the shoulders), hold there for a beat and creep back to the front for the next stroke.
        if (_swimBlend > 0.001f)
        {
            var out_ = StrokeOut();
            var sculling = MathF.Sin(_swimBobTime * 2.3f) * 0.07f * (1f - out_);
            var reachRoll = SwimReachRoll;
            var roll = MathUtils.Lerp(reachRoll, SwimSideRoll, out_);
            var flex = MathUtils.Lerp(SwimReachFlex, SwimSideFlex, out_) + sculling;
            var yaw = MathUtils.Lerp(0f, -SwimSideBehind, out_);
            Blend(ref upper, UpperFrame(side, roll, flex, yaw), _swimBlend);
            Blend(ref fore, ForeFrame(side, MathUtils.Lerp(0.1f, 0.3f, out_), 0f), _swimBlend);
        }

        if (!right) return;

        // Punch: the flare is full at the chamber and gone again at full extension, where the IK
        // frame (the fist on the aim line) takes over, so the elbow swings from out to the side to
        // forward as the arm thrusts.
        if (_mineT < 1f)
        {
            var w = PunchWeights(_mineT);
            var flare = w.Retract * (1f - w.Extend) * (1f - w.Recover);
            Blend(ref upper, UpperFrame(side, PunchChamberRoll, 0f, PunchChamberYaw), flare);
            Blend(ref fore, ForeFrame(side, PunchChamberForearm, 0.1f), flare);
        }

        // Throw: the cocked arm out to the right and behind, forearm up.
        var cock = ThrowArmWeight();
        if (cock > 0.001f)
        {
            Blend(ref upper, UpperFrame(side, ThrowArmRoll, ThrowArmFlex, -ThrowArmBehind), cock);
            Blend(ref fore, ForeFrame(side, ThrowForearmTilt, ThrowForearmUp), cock);
        }
    }

    // How much of the cocked arm the frame shows: eased in with the charge, full while held, and
    // handed to the strike (the base pose racing to the follow-through) over the release.
    private float ThrowArmWeight()
    {
        if (_throwCharge01.HasValue)
            return MathUtils.SmoothStep(_throwCharge01.Value);
        if (_throwT >= ThrowStrikeEnd)
            return 0f;
        var u = _throwT / ThrowStrikeEnd;
        return MathUtils.SmoothStep(_throwReleaseCharge) * (1f - u * u);
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
        public float RootYaw { get; init; }
        public float SwimRoll { get; init; }
        public float DeathPitch { get; init; }
    }

    private Mods ComputeMods()
    {
        var dip = Math.Clamp(_landDip.Value, -0.15f, 0.6f);
        var crouch = _crouchBlend;
        var ground = 1f - _airBlend;
        var move = _moveBlend * ground * (1f - _swimBlend);

        // Slice: the shoulders twist with the sweep and the body shifts its weight onto the lead leg.
        var sliceBody = IsSlicing ? SliceBodySweep() : 0f;
        var sliceTwist = sliceBody * SliceTorsoYaw;
        var sliceRoll = IsSlicing ? _sliceDir * 0.07f * SliceStroke() : 0f;
        // The weight sinks into the strike: knees and hips bend and the torso dips toward the blow.
        var sliceDip = IsSlicing ? MathUtils.Pulse(_sliceT, SliceImpactProgress) : 0f;

        // Throw: the shoulders wind back toward the throwing (right) side and lean back while the
        // arm is cocked, then whip forward through the release and settle.
        float throwLean = 0f;
        float throwTwist = 0f;
        if (_throwCharge01.HasValue)
        {
            var cocked = MathUtils.SmoothStep(_throwCharge01.Value);
            throwLean = 0.18f * cocked;
            throwTwist = -ThrowWindTwist * cocked;
        }
        else if (_throwT < 1f)
        {
            var cocked = MathUtils.SmoothStep(_throwReleaseCharge);
            var strike = MathUtils.EaseOutCubic(MathF.Min(1f, _throwT / ThrowStrikeEnd));
            var settle = 1f - MathUtils.SmoothStep((_throwT - ThrowStrikeEnd) / (1f - ThrowStrikeEnd));
            throwLean = MathUtils.Lerp(0.18f * cocked, -0.3f, strike) * settle;
            throwTwist = MathUtils.Lerp(-ThrowWindTwist * cocked, ThrowFollowTwist, strike) * settle;
        }

        var fall = DeathFall;
        return new Mods
        {
            HipBend = dip * 0.55f + crouch * 0.7f + sliceDip * 0.25f,
            KneeBend = dip * 1.1f + crouch * 1.4f + sliceDip * 0.4f,
            RootYaw = sliceBody * SliceBodyYaw,
            Shoulders = _swayPitch.Value + dip * 0.3f,
            RightShoulderExtra = -_equip.Value * 0.9f,
            TorsoPitch = -dip * 0.2f - crouch * 0.25f + _flinch.Value * 0.3f + throwLean - sliceDip * 0.22f,
            Drop = dip * 0.12f + crouch * 0.17f + sliceDip * 0.06f,
            TorsoYaw = MathF.Sin(_walkPhase) * 0.1f * move + sliceTwist + throwTwist
                       - PunchTwist * PunchWeights(_mineT).Extend,
            // The body also leans a little toward whichever side its shoulders are being pulled out to.
            TorsoRoll = MathF.Cos(_walkPhase) * 0.035f * move + sliceRoll + _flinch.Value * 0.08f
                        - (_pullR[0].Value + _pullL[0].Value) * 0.8f,
            HeadPitch = _flinch.Value * 0.45f - crouch * 0.1f,
            HeadYaw = BodyYawLags && _lookKnown ? -MathUtils.ToRadians(WrapDegrees(_lookYaw - _bodyYaw)) : 0f,
            // The whole body (legs included) tips toward the heading: forward when swimming ahead, back
            // when backing off, rolled toward a strafe, plus a lunge at the burst.
            SwimPitch = _swimBlend * (-0.25f - 0.85f * MathUtils.Clamp01(_swimFwd / 5f)
                                      + 0.45f * MathUtils.Clamp01(-_swimFwd / 5f)
                                      - 0.18f * MathUtils.Pulse(SwimStrokeProgress, 0.08f)),
            SwimRoll = -_swimBlend * Math.Clamp(_swimSide / 5f, -1f, 1f) * 0.6f,
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

    // The torso's rotation (about the hips) for a given forward pitch: what the shoulders ride on.
    private Matrix4x4 TorsoRotation(float torsoPitch)
    {
        var mods = ComputeMods();
        return Matrix4x4.CreateRotationX(torsoPitch) * Matrix4x4.CreateRotationY(mods.TorsoYaw)
               * Matrix4x4.CreateRotationZ(mods.TorsoRoll);
    }

    // A fist position in the view frame as an offset from the shoulder in body space. The shoulder
    // does not rotate with the look - it sits ShoulderX to the side, ShoulderY above the feet and
    // FirstPersonBodyBack behind the eye - so the view-frame point has to be turned back out by the
    // pitch before the arm can be solved to it. The shoulder is where the torso's lean and twist
    // actually carry it (it rides on the torso), not where it rests, or the fist would miss the aim by
    // the few centimetres the torso moved it. Y is the up axis and Z points forward out of the eye, so a
    // point in front has a negative Z here.
    private Vector3 PunchTargetFromShoulder(Vector3 viewFist, float pitchRad, float torsoPitch)
    {
        var cos = MathF.Cos(pitchRad);
        var sin = MathF.Sin(pitchRad);
        var hip = Rig.Parts.TryGetValue("Torso", out var torso) ? torso.Pivot : new Vector3(0f, HipY, 0f);
        // The shoulder is slid in toward the body while the arm is lifted for the punch (the socket).
        var punchWeights = PunchWeights(_mineT);
        var socket = ShoulderSocketInward * punchWeights.Retract * (1f - punchWeights.Recover);
        var shoulderNow = Vector3.Transform(new Vector3(ShoulderX - socket, ShoulderY, 0f) - hip, TorsoRotation(torsoPitch)) + hip;
        var target = new Vector3(
            viewFist.X,
            Camera.EyeHeight + viewFist.Y * cos + viewFist.Z * sin,
            viewFist.Y * sin - viewFist.Z * cos - FirstPersonBodyBack);
        return target - shoulderNow;
    }

    // Shoulder and elbow that put the fist on a view-frame point, for a camera pitched by
    // pitchRad. Beyond the arm's reach the solver pins the elbow straight, which leaves the jab
    // pointing at the target and merely falling short of it.
    private (float Shoulder, float Elbow) PunchTargetJoints(Vector3 viewFist, float pitchRad, float torsoPitch)
    {
        var target = PunchTargetFromShoulder(viewFist, pitchRad, torsoPitch);
        var lateral = MathF.Sqrt(target.X * target.X + target.Z * target.Z);
        return SolveArm(target.Y, -lateral);
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
    private float PunchArmYaw(float pitchRad, float torsoPitch)
    {
        if (_mineT >= 1f)
            return 0f;
        var w = PunchWeights(_mineT);
        var yaw = MathUtils.Lerp(0f, PunchTargetYaw(PunchChamberFist, pitchRad, torsoPitch), w.Retract);
        yaw = MathUtils.Lerp(yaw, PunchTargetYaw(PunchExtendFist, pitchRad, torsoPitch), w.Extend);
        return MathUtils.Lerp(yaw, 0f, w.Recover);
    }

    private Pose WithPunch(in Pose pose, float pitchDeg)
    {
        if (_mineT >= 1f)
            return pose;
        var pitchRad = MathUtils.ToRadians(pitchDeg);
        var w = PunchWeights(_mineT);
        var torsoPitch = pose.TorsoPitch - PunchLean * (1f + _mineWeight * 1.5f) * w.Extend;
        var (shoulder, elbow) = PunchJoints(pose, _mineT, pitchRad, torsoPitch);
        // The arm hangs off the torso, so the torso's lean tilts it too: take that out of the flexion so the
        // fist still lands where the solve put it.
        shoulder -= torsoPitch * w.Retract * (1f - w.Recover);
        return new Pose(pose.LeftHip, pose.RightHip, pose.LeftKnee, pose.RightKnee,
            pose.LeftShoulder - PunchLeftCounter * w.Extend, shoulder, pose.LeftElbow, elbow,
            torsoPitch, pose.Bob);
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
            // Drift the hands toward the view centre so they read at the bottom of the
            // frame instead of out at the edges. Right arm (+X) yaws one way, left the other.
            // The slice sweep rides that same yaw but is applied after the forward pitch
            // (System.Numerics is row-vector, so the leftmost factor runs first), carrying the
            // raised arm through a horizontal arc instead of twisting a hanging one.
            var side = pivot.X > 0f ? 1f : -1f;
            upper = FrameOf(Matrix4x4.CreateFromAxisAngle(swingAxis, shoulder)
                            * Matrix4x4.CreateFromAxisAngle(Vector3.UnitY, side * ArmInwardYaw + sweep + _swayYaw.Value));
            fore = FrameOf(Matrix4x4.CreateFromAxisAngle(swingAxis, elbow));
            ApplyArmFrames(side, ref upper, ref fore);
            // The arm now hangs off the torso, so the torso's twist turns it too. The authored poses (the
            // slice sweep, the cocked throw, the punch) were tuned in body space, so while one is showing the twist
            // is taken back out of the arm's yaw and its hand path stays where it was; an idle arm simply follows
            // the torso round.
            var authored = side > 0f ? MathUtils.Clamp01(MathF.Max(SliceStroke(), MathF.Max(ThrowArmWeight(), PunchActivity()))) : 0f;
            if (authored > 0f && mods.TorsoYaw != 0f)
                upper = FrameOf(Matrix4x4.CreateFromQuaternion(upper) * Matrix4x4.CreateRotationY(-mods.TorsoYaw * authored));
        }

        // A shoulder sits where it rests on the torso, slid in toward the body as the arm lifts (the socket), plus its
        // pull toward the reaching arm, in the torso frame.
        Matrix4x4 UpperArm(Vector3 pivot, Quaternion upper) =>
            Matrix4x4.CreateFromQuaternion(upper)
            * Matrix4x4.CreateTranslation(pivot - Pivot("Torso") + ShoulderPull(pivot.X > 0f)
                + new Vector3(pivot.X > 0f ? -1f : 1f, 0f, 0f) * ShoulderSocketInward * SocketWeight(upper)) * torsoFrame;

        // The legs turn back against the body's twist so the feet stay planted.
        Matrix4x4 Thigh(Vector3 pivot, float hip) =>
            Matrix4x4.CreateFromAxisAngle(swingAxis, hip)
            * Matrix4x4.CreateRotationY(-mods.RootYaw * SliceLegCounter)
            * Matrix4x4.CreateTranslation(pivot) * root;

        var sliceSweep = SliceSweep();
        // The jab swings the arm across to the aim's own side as it extends, and back out on the
        // recovery. PunchArmYaw is absolute while UpperArm adds ArmInwardYaw on its own, so that
        // fixed term is taken back out here.
        var rightArmYaw = sliceSweep + PunchArmYaw(pitchRad, pose.TorsoPitch) - ArmInwardYaw;
        // While slicing the arm is eased from the carrying pose into the extended one (see SliceArmShoulder).
        var sliceStroke = SliceStroke();
        var rightShoulder = MathUtils.Lerp(pose.RightShoulder, SliceArmShoulder, sliceStroke);
        var rightElbow = MathUtils.Lerp(pose.RightElbow, SliceArmElbow, sliceStroke);

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
        var forearm = GetPartMatrix("ForearmR", eyePos, yawDeg, pitchDeg, backOffset);
        var fist = Vector3.Transform(new Vector3(0f, HandDepth(), 0f), forearm);
        var yawRad = -((BodyYawLags && _lookKnown ? _bodyYaw : yawDeg) + 90f) * MathF.PI / 180f;
        var pitchRad = MathUtils.ToRadians(pitchDeg);
        // The jab is thrown along the aim rather than the body, so the tool takes the look pitch
        // and the jab's swing for the length of the swing, then drops both as the arm settles.
        var punch = PunchActivity();
        var rotation = Matrix4x4.CreateRotationX(pitchRad * punch)
                       * Matrix4x4.CreateRotationY(yawRad + PunchArmYaw(pitchRad, GetPose(pitchDeg).TorsoPitch) + _swayYaw.Value);

        // A slice carries the blade by the wrist: its tip points along the forearm (the item's tip is its local +Y,
        // the forearm's hand end its local -Y), cocked up a little and rolled about its length, and it takes over from
        // the body-space orientation as the stroke ramps in, so the blade follows the hand's sweep instead of a
        // separate formula and never turns up in the middle of the cut.
        var carried = IsSlicing ? SliceStroke() : 0f;
        if (carried > 0f)
        {
            var forearmRotation = forearm;
            forearmRotation.Translation = Vector3.Zero;
            var wrist = Matrix4x4.CreateRotationX(MathF.PI) * Matrix4x4.CreateRotationY(SliceBladeRoll * _sliceDir)
                        * Matrix4x4.CreateRotationX(SliceBladeCant) * forearmRotation
                        * Matrix4x4.CreateRotationY(SliceBladeYawOffset * _sliceDir * SliceMainWeight());
            rotation = Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(FrameOf(rotation), FrameOf(wrist), carried));
        }

        return rotation * Matrix4x4.CreateTranslation(fist);
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
