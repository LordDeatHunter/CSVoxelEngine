using System;
using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Interaction;
using VoxelEngine.Juice;
using VoxelEngine.Particles;
using VoxelEngine.Player.Posing;

namespace VoxelEngine.Player;

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

    // Sword attack (items with AttackPattern.Sword): the cut, back cut and chop chain. Each swing has its
    // own length and impact (SwordMotion), and like the slice is a wind-up, a strike and a recovery
    // that equal the idle hold at both ends.

    /// <summary>Seconds one sword swing of <paramref name="swing"/> lasts.</summary>
    public static float SwordDuration(SwordSwing swing) => SwordMotion.Duration(swing);

    /// <summary>Progress through <paramref name="swing"/> at which its damage lands.</summary>
    public static float SwordImpactProgress(SwordSwing swing) => SwordMotion.ImpactProgress(swing);

    /// <summary>The shortest sword swing's length, the least gap any sword swing can follow another at.</summary>
    public static float ShortestSwordSwingSeconds
    {
        get
        {
            var shortest = float.PositiveInfinity;
            foreach (var swing in SwordSwings.All)
                shortest = MathF.Min(shortest, SwordDuration(swing));
            return shortest;
        }
    }

    // Charged throw (right-click in the air with a throwable). While held, the right arm
    // rises forward-up overhead into a cocked pre-throw with the elbow extending on the
    // way up, eased by the charge amount so the raise reads as slow. The cock goes the
    // forward way (never backward): swinging back would drag the hand down past the leg
    // and leave it hanging behind the back. Absolute joint targets (not deltas) so the
    // pose is the same held or empty-handed. On release the arm strikes fast from the
    // exact release pose through a follow-through, then settles slowly back to rest —
    // one direction change, which reads as follow-through rather than a bounce.
    public const float ThrowDuration = 0.55f;
    // A staff cast (cosmetic): the right arm raises the staff over the head-front, thrusts it forward with
    // a small lean, recoils a little and eases back to rest (CastMotion).
    public const float CastDuration = 0.45f;
    // First-person framing: straight-hanging arms sit ~66 degrees below the view center,
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
    private float _swordT = 1f;
    private SwordSwing _swordSwing;
    // Seconds the swing in progress lasts: its own length, or an axe chop's, set to the axe's hit pace
    private float _swordSeconds = 1f;
    // The pose the timed use in progress (or the last one) holds, and a bow's release: the charge it was
    // let go at and its progress (1 = no release playing)
    private UsePose _usePose;
    private float _bowReleaseT = 1f;
    private float _bowReleaseCharge;
    // Hit-stop of the punch, slice and sword clocks: a swing that connected holds at its impact pose
    private readonly SwingHitStop _mineStop = new();
    private readonly SwingHitStop _sliceStop = new();
    private readonly SwingHitStop _swordStop = new();
    private float _throwT = 1f;
    private float _castT = 1f;
    private float? _throwCharge01;
    private float? _useCharge01;
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
    // (see ShoulderReach.PullTarget), through a spring so a sudden thrust or release trails and settles. x, y, z in torso space.
    private readonly Spring[] _pullR = [new(5f, 0.7f), new(5f, 0.7f), new(5f, 0.7f)];
    private readonly Spring[] _pullL = [new(5f, 0.7f), new(5f, 0.7f), new(5f, 0.7f)];
    /// <summary>The farthest a shoulder is ever pulled off its rest place on the torso, in blocks.</summary>
    public const float MaxShoulderPullBlocks = ShoulderReach.MaxShoulderPull;
    private float _crouchBlend;
    private float _swimBlend;
    // Smoothed 0..1 flight amount (flying in Update) and the smoothed flight speed in blocks/s
    private float _flightBlend;
    private float _flightSpeed;
    // Smoothed 0..1 surface-float amount (floating in Update), at most the swim amount
    private float _floatBlend;
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

    /// <summary>The vertical velocity the last Update was given, in blocks/s (the jump pose reads it).</summary>
    internal float AirVelocity => _airVelocity;
    /// <summary>Smoothed 0..1 sprint amount of the airborne pose.</summary>
    internal float AirRun => _airRun;
    /// <summary>The current punch's weight, 0 light to 1 heavy (see Punch).</summary>
    internal float MineWeight => _mineWeight;
    /// <summary>The charge the last throw was released at, 0..1.</summary>
    internal float ThrowReleaseCharge => _throwReleaseCharge;
    /// <summary>Seconds of Update time, driving the idle arm drift.</summary>
    internal float PoseTime => _time;
    /// <summary>Seconds of Update time driving the swim bob, sculling and flutter.</summary>
    internal float SwimBobTime => _swimBobTime;
    /// <summary>Smoothed forward swim velocity.</summary>
    internal float SwimForward => _swimFwd;
    /// <summary>Smoothed sideways swim velocity.</summary>
    internal float SwimSide => _swimSide;
    /// <summary>Whether SetLook has been called at least once.</summary>
    internal bool LookKnown => _lookKnown;
    /// <summary>The last reported look yaw, in degrees.</summary>
    internal float LookYaw => _lookYaw;
    /// <summary>The trailing body's own yaw, in degrees (BodyYaw is this or the look, per BodyYawLags).</summary>
    internal float TrailingBodyYaw => _bodyYaw;
    /// <summary>The arm lag behind the look's yaw (the sway spring's value).</summary>
    internal float SwayYaw => _swayYaw.Value;
    /// <summary>The arm lag behind the look's pitch (the sway spring's value).</summary>
    internal float SwayPitch => _swayPitch.Value;
    /// <summary>The right shoulder pull spring's x value (unclamped).</summary>
    internal float RightPullX => _pullR[0].Value;
    /// <summary>The left shoulder pull spring's x value (unclamped).</summary>
    internal float LeftPullX => _pullL[0].Value;

    /// <summary>The swim stroke shape and the swim and float leg pose.</summary>
    internal SwimPose Swim { get; }

    /// <summary>The creative-flight leg sway, trail and lean.</summary>
    internal FlightPose Flight { get; }

    /// <summary>A bow's two-handed draw.</summary>
    internal BowPose Bow { get; }

    /// <summary>The throw and timed-use arm overrides.</summary>
    internal ThrowPose ThrowArm { get; }

    /// <summary>The body-wide secondary motion.</summary>
    internal SecondaryMotion Secondary { get; }

    /// <summary>The jab's weights and arm solve.</summary>
    internal PunchPose Jab { get; }
    /// <summary>The pose-specific arm frames.</summary>
    internal ArmOverlays Overlays { get; }
    /// <summary>The shoulder pull targets.</summary>
    internal ShoulderReach Reach { get; }
    /// <summary>Assembles the joint pose.</summary>
    internal PoseBuilder Poses { get; }
    /// <summary>The body-part and hand-anchor matrices.</summary>
    internal BodyPartMatrices Matrices { get; }

    /// <summary>Builds the posing components, which read this model's state each call and keep none of their own.</summary>
    public PlayerModel()
    {
        Swim = new SwimPose(this);
        Flight = new FlightPose(this);
        Bow = new BowPose(this);
        ThrowArm = new ThrowPose(this);
        Secondary = new SecondaryMotion(this);
        Jab = new PunchPose(this);
        Overlays = new ArmOverlays(this);
        Reach = new ShoulderReach(this);
        Poses = new PoseBuilder(this);
        Matrices = new BodyPartMatrices(this);
    }

    /// <summary>Whether the right hand currently holds something; eases the carrying pose in and out.</summary>
    public bool HoldingItem { get; set; }

    /// <summary>Raises the carrying arm for a hanging lantern.</summary>
    public bool HoldingLantern { get; set; }
    internal float FirstPersonBack => HoldingLantern ? 0f : FirstPersonBodyBack;
    internal float CarryShoulder => HoldingLantern ? LanternConfig.CarryShoulder : HoldShoulder;
    internal float CarryElbow => HoldingLantern ? LanternConfig.CarryElbow : HoldElbow;

    /// <summary>Smoothed 0..1 carrying amount.</summary>
    public float HoldBlend => _holdBlend;

    /// <summary>The shape being posed: pivots, cubes and skin. Swapped for the file rig at startup.</summary>
    public PlayerRig Rig { get; set; } = PlayerRig.BuiltIn();

    /// <summary>0 when a mine hit just landed, 1 when the hand is back at rest.</summary>
    public float MineT => _mineT;

    /// <summary>Whether a slice swing is currently playing.</summary>
    public bool IsSlicing => _sliceT < 1f;

    /// <summary>Whether a sword swing is currently playing.</summary>
    public bool IsSwordSwinging => _swordT < 1f;

    /// <summary>Progress of the active sword swing: 0 at the wind-up, 1 when it has finished.</summary>
    public float SwordProgress => _swordT;

    /// <summary>The sword swing playing, or the last one played.</summary>
    public SwordSwing ActiveSwordSwing => _swordSwing;

    /// <summary>What the right arm, the body and the held blade do this frame for the blade swing in progress (the
    /// slice's or the sword's), or <see cref="BladeSwingFrame.None"/> with neither playing.</summary>
    internal BladeSwingFrame Blade => IsSlicing
        ? SliceMotion.FrameAt(_sliceT, _sliceDir)
        : IsSwordSwinging ? SwordMotion.FrameAt(_swordT, _swordSwing) : BladeSwingFrame.None;

    /// <summary>Whether a throw swing is currently playing.</summary>
    public bool IsThrowing => _throwT < 1f;

    /// <summary>Progress of the active throw swing: 0 at release, 1 when it has finished.</summary>
    public float ThrowProgress => _throwT;

    /// <summary>Whether a staff cast's gesture is currently playing.</summary>
    public bool IsCasting => _castT < 1f;

    /// <summary>Progress of the cast gesture: 0 as it starts, 1 when it has finished (also with none playing).</summary>
    public float CastProgress => _castT;

    /// <summary>Charge the arm is cocked to, or null when no throw is being aimed.</summary>
    public float? ThrowCharge => _throwCharge01;

    /// <summary>The timed-use pose's charge 0..1, or null when no use is charging (see
    /// <see cref="ThrowPose.WithUse"/>).</summary>
    public float? UseCharge => _useCharge01;

    /// <summary>Progress of the active slice swing: 0 at the wind-up, 1 when it has finished.</summary>
    public float SliceProgress => _sliceT;

    /// <summary>True while a swing that connected is held at its impact pose (see <see cref="HitStop"/>).</summary>
    public bool IsHitStopped => _mineStop.IsHolding || _sliceStop.IsHolding || _swordStop.IsHolding;

    /// <summary>
    /// The swing in progress (the slice or sword swing if one plays, else the punch) connected with a mob: it
    /// holds at its impact pose for <paramref name="seconds"/> once it gets there, then replays the rest faster so
    /// it still ends on time (<see cref="SwingHitStop"/>). Ignored with no swing in progress, so a call
    /// can never freeze a later swing; a new swing drops any hold the last one had.
    /// </summary>
    public void HitStop(float seconds)
    {
        if (IsSlicing)
            _sliceStop.Request(seconds);
        else if (IsSwordSwinging)
            _swordStop.Request(seconds);
        else if (_mineT < 1f)
            _mineStop.Request(seconds);
    }

    /// <summary>
    /// Degrees the pose adds to the held item's own display rotation, in the item JSON's <c>[pitch, yaw, roll]</c>
    /// order (what <c>Item.DisplayMatrix</c> and the item debugger's Rot sliders take), so a value found by sliding
    /// them at a frame of an animation transfers here unchanged. Zero at rest; through the main part of a blade swing
    /// it is that swing's mid-cut turn (<see cref="SliceMotion.SliceBladeDisplayYawDegrees"/>,
    /// <see cref="SliceMotion.SliceBladeDisplayRollDegrees"/> for the slice, the yaw mirrored for the return swing;
    /// <see cref="SwordMotion"/>'s for the sword).
    /// </summary>
    public Vector3 HeldItemRotationOffset => Blade.DisplayOffset;

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

    /// <summary>Smoothed 0..1 flying amount (<c>flying</c> in <see cref="Update"/>).</summary>
    public float FlightBlend => _flightBlend;

    /// <summary>The smoothed flight speed as a 0..1 share of the speed the pose is fully trailed at
    /// (see <see cref="FlightPose.SpeedShare"/>).</summary>
    public float FlightSpeedShare => FlightPose.SpeedShare(_flightSpeed);

    /// <summary>
    /// Smoothed 0..1 surface-float amount: eases in while <c>floating</c> in <see cref="Update"/> with no
    /// stroke under way, out during a stroke and once the body stops floating. Never above
    /// <see cref="SwimBlend"/>, since the float pose is laid over the swim pose.
    /// </summary>
    public float FloatBlend => MathF.Min(_floatBlend, _swimBlend);

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

    /// <summary>The gait clock in radians, only ever growing: the hips swing by its sine, so a foot
    /// strikes the ground each time that sine peaks, at π/2 + kπ (twice a stride). The footstep dust
    /// fires on those crossings (<see cref="FootstepDust.FootfallsBetween"/>).</summary>
    public float WalkPhase => _walkPhase;

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
    /// The yaw (degrees) the body, and so the hand anchor, is posed at for the look yaw
    /// <paramref name="yawDeg"/> passed to <see cref="GetPartMatrix"/>: the trailing body's own yaw
    /// while <see cref="BodyYawLags"/> holds and a look is known, the passed yaw otherwise.
    /// </summary>
    internal float DrawnBodyYaw(float yawDeg) => BodyYawLags && _lookKnown ? _bodyYaw : yawDeg;

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

    internal static float WrapDegrees(float degrees) => degrees - MathF.Round(degrees / 360f) * 360f;

    /// <summary>
    /// Advances the walk cycle and the mine swing. <paramref name="horizontalSpeed"/> is in
    /// blocks/s (the camera's XZ displacement per second); standing still eases the limbs
    /// back to rest instead of snapping. <paramref name="sprinting"/> selects the larger
    /// run cycle, and <paramref name="grounded"/> gates both it and the jump pose — callers
    /// pass true while swimming or flying so those stay out of the air pose.
    /// <paramref name="verticalVelocity"/> in blocks/s shapes the jump: rising tucks the
    /// knees, falling extends toward landing. <paramref name="floating"/> (with
    /// <paramref name="swimming"/>) is a body at rest at the top of deep water
    /// (<see cref="WaterPhase.Float"/>): the upright surface-float pose eases in over the swim pose,
    /// except while a stroke is under way. <paramref name="flying"/> is creative flight, whose pose
    /// (the legs trailing, the lean) follows <paramref name="flightSpeed"/>, the body's whole speed in
    /// blocks/s.
    /// </summary>
    public void Update(float deltaTime, float horizontalSpeed, bool sprinting = false, bool grounded = true,
        float verticalVelocity = 0f, bool sneaking = false, bool swimming = false,
        float swimForward = 0f, float swimSide = 0f, bool floating = false,
        bool flying = false, float flightSpeed = 0f)
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
        var pullR = Reach.PullTarget(right: true);
        var pullL = Reach.PullTarget(right: false);
        for (var i = 0; i < 3; i++)
        {
            _pullR[i].Step(pullR[i], deltaTime);
            _pullL[i].Step(pullL[i], deltaTime);
        }
        _crouchBlend = MathUtils.Damp(_crouchBlend, sneaking ? 1f : 0f, 12f, deltaTime);
        _swimBlend = MathUtils.Damp(_swimBlend, swimming ? 1f : 0f, 6f, deltaTime);
        _flightBlend = MathUtils.Damp(_flightBlend, flying ? 1f : 0f, PlayerConfig.FlightBlendRate, deltaTime);
        _flightSpeed = MathUtils.Damp(_flightSpeed, flying ? flightSpeed : 0f, PlayerConfig.FlightSpeedEaseRate, deltaTime);
        _swimBobTime += deltaTime;
        _swimT += deltaTime;
        if (swimming && AutoSwimStroke && _swimT >= PlayerPhysics.SwimStrokeSeconds && horizontalSpeed > 0.8f)
            _swimT = 0f;
        // The float pose shows at rest; a stroke (a floater swimming off) hands the body to the swim pose
        _floatBlend = MathUtils.Damp(_floatBlend,
            floating && swimming && SwimStrokeProgress >= 1f ? 1f : 0f, SwimPose.FloatBlendRate, deltaTime);
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

        var target = MathUtils.Clamp01(horizontalSpeed / PoseBuilder.WalkSpeedRef);
        _moveBlend = MathUtils.Damp(_moveBlend, target, PoseBuilder.MoveBlendRate, deltaTime);
        var runTarget = sprinting && grounded && horizontalSpeed > 0.5f ? 1f : 0f;
        _runBlend = MathUtils.Damp(_runBlend, runTarget, PoseBuilder.RunBlendRate, deltaTime);
        _airBlend = MathUtils.Damp(_airBlend, grounded ? 0f : 1f, PoseBuilder.AirBlendRate, deltaTime);
        _airRun = MathUtils.Damp(_airRun, sprinting && horizontalSpeed > 0.5f ? 1f : 0f,
            PoseBuilder.RunBlendRate, deltaTime);
        _airVelocity = verticalVelocity;
        _holdBlend = MathUtils.Damp(_holdBlend, HoldingItem ? 1f : 0f, PoseBuilder.MoveBlendRate, deltaTime);

        // Stride rate grows with speed so feet roughly keep up with the ground, plus an
        // extra kick while running so the cadence lifts beyond a sped-up walk.
        _walkPhase += deltaTime * PoseBuilder.GaitSpeedScale * (4f + horizontalSpeed * 0.9f + _runBlend * PoseBuilder.RunStrideBoost);

        // The punch's impact pose is full extension, the slice's where the sweep crosses the aim
        if (_mineT < 1f)
            _mineT = _mineStop.Advance(_mineT, deltaTime, _mineSpeed / MineDuration, PunchPose.PunchStrikeEnd);

        if (_sliceT < 1f)
            _sliceT = _sliceStop.Advance(_sliceT, deltaTime, 1f / SliceDuration, SliceImpactProgress);

        if (_swordT < 1f)
            _swordT = _swordStop.Advance(_swordT, deltaTime, 1f / _swordSeconds, SwordImpactProgress(_swordSwing));

        if (_throwT < 1f)
            _throwT = Math.Min(1f, _throwT + deltaTime / ThrowDuration);

        if (_castT < 1f)
            _castT = Math.Min(1f, _castT + deltaTime / CastDuration);

        if (_bowReleaseT < 1f)
            _bowReleaseT = Math.Min(1f, _bowReleaseT + deltaTime / BowPose.ReleaseSeconds);
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
        _mineStop.Reset();
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
        _sliceStop.Reset();
        // One blade swing at a time: a slice cuts short a sword swing still playing (the item changed)
        _swordT = 1f;
    }

    /// <summary>
    /// Starts one swing of the sword's chain (<see cref="SwordSwing"/>). Its arm, body and blade ride
    /// <see cref="SwordProgress"/> through <see cref="SwordMotion"/>; a slice still playing is cut short.
    /// </summary>
    public void StartSword(SwordSwing swing)
    {
        _swordSwing = swing;
        _swordSeconds = SwordDuration(swing);
        _swordT = 0f;
        _swordStop.Reset();
        _sliceT = 1f;
    }

    /// <summary>
    /// Starts an axe's chop (<see cref="SwordSwing.AxeChop"/>) lasting <paramref name="seconds"/>, the
    /// pace of the axe's hits, so the blow comes down as a hit lands and the next wind-up follows at once.
    /// </summary>
    public void StartAxeChop(float seconds)
    {
        StartSword(SwordSwing.AxeChop);
        _swordSeconds = MathF.Max(seconds, 0.05f);
    }

    /// <summary>Starts the slow overhead blow of an axe splitting a log on a stump (<see cref="SwordSwing.AxeSplit"/>).</summary>
    public void StartAxeSplit() => StartSword(SwordSwing.AxeSplit);

    /// <summary>Starts the staff cast's gesture (<see cref="CastMotion"/>): the staff raised and thrust forward. Cosmetic.</summary>
    public void Cast() => _castT = 0f;

    public void StartThrowCharge() => _throwCharge01 = 0f;

    /// <summary>Starts the timed-use pose (see <see cref="VoxelEngine.Interaction.RightClickAction.TryBeginUseCharge()"/>).</summary>
    public void StartUseCharge(UsePose pose = UsePose.Raise)
    {
        _usePose = pose;
        _useCharge01 = 0f;
        _bowReleaseT = 1f;
    }

    /// <summary>The pose the timed use in progress (or the last one) holds.</summary>
    internal UsePose UsePoseActive => _usePose;

    /// <summary>The charge a bow is drawn to (the use charge of a <see cref="UsePose.Bow"/> use), or null with no draw.</summary>
    public float? BowCharge => _usePose == UsePose.Bow ? _useCharge01 : null;

    /// <summary>Progress of a bow's release, 0 at the let-go to 1 when it has settled (also with none playing).</summary>
    public float BowReleaseProgress => _bowReleaseT;

    /// <summary>The charge the last bow release was let go at, 0..1.</summary>
    public float BowReleaseCharge => _bowReleaseCharge;

    /// <summary>Advances the timed-use pose; <paramref name="charge01"/> is the hold's share.</summary>
    public void UpdateUseCharge(float charge01)
    {
        if (_useCharge01.HasValue)
            _useCharge01 = MathUtils.Clamp01(charge01);
    }

    /// <summary>Lets the timed-use pose go; the arm eases back to whatever it was doing.</summary>
    public void CancelUseCharge()
    {
        // A bow let go from a real draw snaps back instead of just dropping the pose
        if (_usePose == UsePose.Bow && _useCharge01 is > BowPose.MinReleaseCharge)
        {
            _bowReleaseCharge = _useCharge01.Value;
            _bowReleaseT = 0f;
        }
        _useCharge01 = null;
    }

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
    public Pose GetPose(float pitchDeg = 0f) => Poses.GetPose(pitchDeg);

    /// <summary>
    /// Where a shoulder joint sits relative to its rest place on the torso: the spring-smoothed pull toward where the
    /// arm reaches, in torso space (x right, y up, z back), at most <see cref="ShoulderReach.MaxShoulderPull"/> long.
    /// </summary>
    public Vector3 ShoulderPull(bool right)
    {
        var springs = right ? _pullR : _pullL;
        var pull = new Vector3(springs[0].Value, springs[1].Value, springs[2].Value);
        var length = pull.Length();
        return length > ShoulderReach.MaxShoulderPull ? pull * (ShoulderReach.MaxShoulderPull / length) : pull;
    }

    /// <summary>
    /// World matrices for every part, keyed by mesh name (see <see cref="BodyParts"/>).
    /// Thigh/Shin/UpperArm/Forearm entries are one side's; the caller draws each twice
    /// with the Left/Right matrices. <paramref name="eyePos"/> is the camera eye (feet are
    /// <see cref="Camera.EyeHeight"/> below it), <paramref name="yawDeg"/>/<paramref name="pitchDeg"/>
    /// the camera look. <paramref name="backOffset"/> slides the whole body away from the
    /// facing direction (first-person framing; the shadow pass leaves it at 0).
    /// </summary>
    public Matrix4x4 GetPartMatrix(string part, Vector3 eyePos, float yawDeg, float pitchDeg, float backOffset = 0f)
        => Matrices.GetPartMatrix(part, eyePos, yawDeg, pitchDeg, backOffset);

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
        => Matrices.GetHandAnchorMatrix(eyePos, yawDeg, backOffset, pitchDeg);

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
