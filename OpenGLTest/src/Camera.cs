using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace silkgltriangle;

/// <summary>F5 camera modes, cycled FirstPerson -> Shoulder -> Orbit -> FirstPerson.</summary>
public enum CameraMode
{
    FirstPerson,
    Shoulder,
    Orbit,
}

public class Camera : IMobTarget
{
    private readonly PlayerPhysics _physics = new();
    private readonly IWindow? window;
    private readonly IMouse? mouse;
    public Vector3 Position { get; private set; }
    public float Yaw { get; private set; } = SpawnLook.X;
    public float Pitch { get; private set; } = SpawnLook.Y;
    public bool IsGrounded { get; private set; }
    public bool IsFlying { get; private set; }
    /// <summary>True while sprinting on foot with movement input; drives the run cycle and roll (the FOV
    /// widening reads the sprint key itself, so a flight sprint widens too: see <see cref="SprintFov"/>).</summary>
    public bool IsSprinting { get; private set; }
    /// <summary>True while crouched on foot (Left Ctrl): slower, a lowered view and the model's crouch pose.
    /// The body and its collision keep their size, so the server's movement rules are unaffected.</summary>
    public bool IsSneaking { get; private set; }
    /// <summary>Strokes the swimmer has started so far; moves on each one (see <c>PlayerPhysics.SwimStrokeSeconds</c>).</summary>
    public int SwimStrokeCount => _physics.SwimStrokeCount;
    /// <summary>Current vertical FOV in degrees: the base FOV plus the eased sprint widening plus the punch pinch.</summary>
    public float CurrentFov => _currentFov;
    /// <summary>The eased sprint widening in degrees, 0 at rest (see <see cref="SprintFov"/>).</summary>
    public float SprintFovDegrees => _sprintFov;
    /// <summary>
    /// Creative mode (toggled with [C]): walking behaves like survival, but block placing
    /// costs nothing, breaking drops nothing, and double-tapping Space toggles flight.
    /// Flight itself is noclip — it passes through walls.
    /// </summary>
    public bool IsCreative { get; private set; }
    /// <summary>
    /// Over-shoulder third-person view (F5 mode 2). The player body stays where the
    /// physics put it (<see cref="Position"/> is still the head); the view eye moves behind
    /// + to the side (<see cref="RenderEye"/>) and tilts back onto the aim point
    /// (<see cref="ViewDirection"/>), which <see cref="Raycast"/> also fires along, so the
    /// highlight lands under the crosshair. Movement, collision and swimming keep reading
    /// <see cref="Position"/>.
    /// </summary>
    public bool IsShoulderSurf => Mode != CameraMode.FirstPerson;
    /// <summary>Active F5 camera mode.</summary>
    public CameraMode Mode { get; private set; } = CameraMode.FirstPerson;
    /// <summary>
    /// Centered orbit view (F5 mode 3): same rig as shoulder-surf but centered, with the
    /// camera orbiting freely while the player body keeps its own facing
    /// (<see cref="PlayerYaw"/>). Moving or interacting turns the player toward the motion
    /// or the aim, fast-lerped rather than snapped.
    /// </summary>
    public bool IsOrbit => Mode == CameraMode.Orbit;
    /// <summary>
    /// Yaw the player body faces, in degrees. Tracks the camera in first person and
    /// shoulder-surf; in orbit mode it lerps toward the movement direction or, after a
    /// click, toward the camera aim (see <see cref="NotifyPlayerAction"/>).
    /// </summary>
    public float PlayerYaw => IsOrbit ? _orbitPlayerYaw : Yaw;
    /// <summary>View eye: the head in first person, the pulled-in shoulder camera in third.</summary>
    public Vector3 RenderEye => Position + _shoulderOffset;
    /// <summary>
    /// F5 transition state, 0 in first person and 1 in shoulder-surf, eased over
    /// <c>ShoulderTransitionSeconds</c>. The camera offset, the view direction and the
    /// head opacity all ride this, so toggling never snaps.
    /// </summary>
    public float ShoulderBlend => MathUtils.SmoothStep(_shoulderBlendT);
    /// <summary>
    /// Direction the screen looks along. Equals the head facing in first person; in
    /// shoulder-surf it tilts from the offset camera back onto the crosshair aim point,
    /// which is also the direction <see cref="Raycast"/> fires.
    /// </summary>
    public Vector3 ViewDirection => _shoulderViewDir.NormalizeSafeOr(GetForwardVector(), 1e-8f);
    /// <summary>
    /// The unit head ray the last <see cref="Raycast(float, ChunkManager, out Vector3, out Vector3, out int, bool)"/>
    /// fired from <see cref="Position"/>: the facing in first person, the head's re-cast toward the
    /// crosshair's surface point in third person. A multiplayer client sends it with an intent, so
    /// the server's first-person ray from the same eye lands on the same cell.
    /// </summary>
    public Vector3 LastAimDirection { get; private set; } = -Vector3.UnitZ;
    [Obsolete("Renamed to IsCreative.")]
    public bool IsNoclip => IsCreative;
    public float VerticalVelocity => _physics.VerticalVelocity;

    /// <summary>
    /// Movement-speed multiplier from the pass-through blocks the body overlaps this frame: the
    /// smallest block <see cref="BlockType.MovementSpeed"/>, or 1 when none. Flight still reports
    /// the sampled value even though it does not apply it, so the debug line can tell a flyer from
    /// a walker standing in a block with no movement speed.
    /// </summary>
    public float MovementFactor { get; private set; } = 1f;
    public float GroundFriction { get; private set; } = BlockType.DefaultFriction;
    public Vector2 HorizontalVelocity => _physics.HorizontalVelocity;

    /// <summary>
    /// Whether the player is deep enough in a fluid to swim: some cell of the body's footprint
    /// in the feet..feet+<see cref="SwimDepth"/> band has a fluid surface above the feet. A thin
    /// flowing skirt underfoot is waded through, not swum in.
    /// </summary>
    public bool IsInWater { get; private set; }

    /// <summary>Whether the eye itself sits below a fluid surface — the source of truth for the
    /// underwater tint, which must not fade in while only the legs are wet.</summary>
    public bool IsEyeInWater { get; private set; }

    /// <summary>Whether the eye sits inside a glowing fluid (lava): the underwater tint
    /// turns red instead of blue. A fluid's glow is what tells them apart, so a new
    /// emissive fluid gets the lava treatment without naming it here.</summary>
    public bool IsEyeInLava { get; private set; }

    /// <summary>World Y of the water surface over the eye's column
    /// (<see cref="ChunkManager.GetFluidColumnTopGlobal"/>), which the underwater rays march
    /// against. Sticky: it keeps its last value after the eye leaves the water, so the rays'
    /// fade-out does not jump to another plane.</summary>
    public float EyeWaterSurfaceY { get; private set; } = WorldGenerationConfig.SeaLevel + FluidState.SourceHeight;

    /// <summary>Whether any cell the body overlaps holds an emissive fluid (lava): the
    /// damage volume for lava burns. Glow tells lava apart from water, exactly like
    /// <see cref="IsEyeInLava"/>, so a new emissive fluid burns unnamed.</summary>
    public bool IsInLava { get; private set; }

    private const float BobDampRate = 8f;
    private const float RunBlendRate = 6f;
    private const float WalkBobHeight = 0.035f;
    private const float RunBobHeight = 0.06f;
    private const float WalkBobStrafe = 0.02f;
    private const float RunBobStrafe = 0.035f;
    private const float RunRoll = 0.02f;
    // Near plane sets depth precision at distance (error ~ z^2 / (near * 2^24)); it must be
    // large enough that stacked LOD levels ~4 blocks apart don't z-fight at 2048 blocks.
    // Collision keeps geometry at least PlayerRadius (0.3) away, so 0.2 never clips.
    private const float NearPlane = 0.2f;
    private const float FarPlane = 4096f; // past the outermost LOD ring (2048 blocks + terrain height)
    private static float DefaultFov => GameSettings.FieldOfView;
    private static float MouseSensitivity => GameSettings.MouseSensitivity;

    private const float BaseSpeed = 8f;
    // Shift while walking is a regular sprint; the giga boost is flight-only.
    private const float SprintMultiplier = 1.5f;
    private const float FlyBoostMultiplier = 10f;
    private const float Gravity = 34f;
    private const float JumpSpeed = 11f;
    private const float PlayerHeight = 1.8f;
    /// <summary>Eye above the feet; the player model roots itself here.</summary>
    public const float EyeHeight = 1.62f;
    private const float PlayerRadius = 0.30f;
    private const float CollisionStepSize = 0.05f;
    // How fast a mob knockback shove decays; larger stops the slide sooner.
    private const float KnockbackDamping = 6f;
    private const float GroundMinResponse = 0.02f;
    private const float GroundSlideScale = 10f;
    private const float AirAccelRate = 6f;
    private const float SwimAccelRate = 4.5f;
    private const float FlyAccelRate = 6f;

    // Auto-step heights: sprinting runs up a full block, plain walking only half a block,
    // so a one-block terrain step still has to be jumped (or run at) on foot. StepClearance
    // is a hair of extra lift so a block top the feet land exactly on is not read as a
    // collision because of float rounding.
    private const float WalkStepHeight = 0.5f;
    private const float SprintStepHeight = 1f;
    private const float StepClearance = 0.001f;

    // Swimming. Water has no gravity term: vertical speed eases toward a target instead, so it
    // stays capped by SwimUpSpeed/SinkSpeed however long the player stays under.
    private const float SwimSpeedFactor = 0.5f;
    private const float SwimUpSpeed = 4f;
    private const float SinkSpeed = -2f;
    private const float WaterDrag = 6f;
    // Pushing into a bank while holding Space lifts the player over it, so getting out of
    // water never needs a ramp
    private const float SwimExitBoost = 6f;
    // How far above the feet a fluid surface has to reach before it counts as swimmable
    private const float SwimDepth = 0.9f;
    // How hard a flow carries a swimmer along it, in blocks/s
    private const float FlowPushSpeed = 2f;

    // Shoulder-surf framing, in blocks, relative to the head eye. RPG-style:
    // behind, above and out to the right shoulder. The view direction stays parallel
    // to the look so aiming (which still fires from the head) keeps its feel.
    private const float ShoulderBackDist = 3.5f;
    private const float ShoulderHeightOffset = 0.7f;
    private const float ShoulderSideOffset = 0.9f;
    // How far short of a blocking wall the pulled-in camera stops, and the march step.
    private const float ShoulderPullMargin = 0.3f;
    private const float ShoulderProbeStep = 0.175f;
    // The camera converges on this point ahead of the head, so the crosshair sits on
    // the same block the raycast hits. Matches World.InteractionReach.
    private const float ShoulderAimDistance = 10f;

    // Two Space presses within this window toggle flight
    private const float DoubleTapWindow = 0.3f;

    // F5 transition: the camera glides and the head fades over this long, eased.
    internal const float ShoulderTransitionSeconds = 0.5f;
    // Orbit player turn speed, in exponential-damp units per second: fast but visibly lerped.
    internal const float OrbitTurnRate = 10f;
    // Lateral glide rate for the shoulder<->orbit side offset, same ballpark as the F5 glide.
    private const float OrbitSideRate = 8f;

    private bool _f5WasPressed;
    private Vector3 _shoulderOffset = Vector3.Zero;
    private Vector3 _shoulderViewDir = Vector3.Zero;
    // Linear 0..1 timer behind ShoulderBlend; toggling mid-glide reverses smoothly.
    private float _shoulderBlendT;
    // Orbit body facing (degrees) plus its smoothed lateral rig offset, both glided.
    private float _orbitPlayerYaw;
    private float _shoulderSide = ShoulderSideOffset;
    private bool _hasActionTarget;
    private float _actionYaw;

    // Set to "x,y,z" to start the eye at an exact spot instead of the surface spawn
    private const string SpawnOverrideVariable = "VOXEL_SPAWN";

    // Set to "yaw,pitch" in degrees to start facing an exact direction
    private const string LookOverrideVariable = "VOXEL_LOOK";

    // Yaw 0 looks down +X, so the default -90 faces -Z, level with the horizon
    private static readonly Vector2 DefaultLook = new(-90f, 0f);

    // Parsed once for the whole process, and read by the Yaw/Pitch initializers above
    private static readonly Vector2 SpawnLook = SpawnOrientation();

    private float _aspectRatio = 16f / 10f;
    private float _currentFov = DefaultFov;
    private float _bobPhase;
    private float _bobBlend;
    private float _runBlend;
    private float _bobHeight;
    private float _bobStrafe;
    private float _roll;

    // Camera juice. Every effect folds into the fields the view cache is already keyed on
    // (_bobHeight, _roll, _currentFov), so none of it needs a cache key of its own: a landing
    // dips the view, a hit kicks its roll toward the blow, a landed punch pinches the FOV, a
    // crouch lowers the eye and a death tips the view over.
    private Spring _landDip = new(3f, 0.45f);
    private Spring _kickRoll = new(4f, 0.35f);
    private Spring _fovPunch = new(5f, 0.4f);
    // Extra degrees of the sprint widening, eased by SprintFov.Step; added to the FOV, never set over it
    private float _sprintFov;
    // Roll toward the strafe side in radians, eased by StrafeTilt.Step; added to _roll, never set over it
    private float _strafeTilt;
    private float _sneakBlend;
    private float _prevVerticalVelocity;
    private bool _wasGrounded;
    private float _deathProgress;
    private const float SneakEyeDrop = 0.22f;
    private const float LandDipPerSpeed = 0.55f;
    private const float LandDipScale = 0.8f;
    private const float DeathRollRadians = 1.2f;
    private const float DeathDropBlocks = 1.1f;

    /// <summary>The landing spring's displacement (positive = the view is dipped).</summary>
    public float LandingDip => _landDip.Value;

    /// <summary>The hit-kick roll in radians.</summary>
    public float KickRoll => _kickRoll.Value;

    /// <summary>The eased strafe tilt in radians: positive leans the view right (see <see cref="StrafeTilt"/>).</summary>
    public float StrafeTiltRoll => _strafeTilt;

    /// <summary>0..1 how far the death view has fallen over; set by the world while a death plays.</summary>
    public float DeathProgress
    {
        get => _deathProgress;
        set => _deathProgress = MathUtils.Clamp01(value);
    }

    /// <summary>
    /// A hit from the world direction <paramref name="awayHorizontal"/> (the way the blow pushes the
    /// player): the view rolls away from the side it came from and dips, springing back.
    /// </summary>
    public void AddHitKick(Vector3 awayHorizontal, float strength = 1f)
    {
        var forward = GetForwardVector();
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var side = awayHorizontal.LengthSquared() > 1e-6f
            ? Vector3.Dot(Vector3.Normalize(new Vector3(awayHorizontal.X, 0f, awayHorizontal.Z)), right)
            : 0f;
        // Pushed to the right (blow from the left) rolls the view clockwise.
        _kickRoll.Impulse((0.9f * side + 0.25f) * strength);
        _landDip.Impulse(5f * strength);
    }

    /// <summary>Pinches the FOV for a beat: a negative <paramref name="degrees"/> is a zoom-in (an impact).</summary>
    public void PunchFov(float degrees) => _fovPunch.Impulse(degrees * 14f);

    // PERF_TODO R7: GetViewProjection ran at 22 call sites a frame, rebuilding view and
    // projection every time. The cache is validated against its own inputs rather than
    // dirty-flagged: the key holds exactly the mutable state GetViewMatrix and
    // GetProjectionMatrix read, compared bit-for-bit, so a hit means the recomputation
    // would have produced a bit-identical matrix. Render and update run on one thread
    // (AGENTS.md), so these are plain fields with no lock.
    private ViewProjKey _vpKey;
    private Matrix4x4 _viewProj;
    private Frustum _viewFrustum;
    private bool _vpValid;
    private Vector2 _lastMousePosition;
    private bool _firstMouseMovement = true;
    private bool _spaceWasPressed;
    private bool _creativeWasPressed;
    private float _timeSinceSpacePress = float.MaxValue;
    // Eye position standing on the terrain at the origin, so the player never starts buried
    private static Vector3 SpawnPosition()
    {
        // Dev override: VOXEL_SPAWN="x,y,z" drops the eye exactly there, so a spot worth
        // inspecting (a cave, a ravine) can be looked at without flying out to it first.
        var spawnOverride = Environment.GetEnvironmentVariable(SpawnOverrideVariable);
        if (!string.IsNullOrWhiteSpace(spawnOverride))
        {
            if (TryParseSpawnOverride(spawnOverride, out var overridePosition))
                return overridePosition;

            Log.WriteLine(
                $"Ignoring {SpawnOverrideVariable}=\"{spawnOverride}\": expected three comma-separated numbers, e.g. -40,-107,-75");
        }

        // On the water surface where the origin column is flooded, so the player never starts
        // submerged (or, once swimming exists, sinking)
        var surface = Math.Max(ColumnCache.GetHeights(0, 0)[0], WorldGenerationConfig.SeaLevel) + 1;
        return new Vector3(0.5f, surface + EyeHeight, 0.5f);
    }

    private static bool TryParseSpawnOverride(string value, out Vector3 position)
    {
        position = Vector3.Zero;

        var parts = value.Split(',');
        if (parts.Length != 3)
            return false;

        Span<float> components = stackalloc float[3];
        for (var i = 0; i < 3; i++)
        {
            if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out components[i]))
                return false;
        }

        position = new Vector3(components[0], components[1], components[2]);
        return true;
    }

    // Facing at startup, as (yaw, pitch) in degrees
    private static Vector2 SpawnOrientation()
    {
        // Dev override: VOXEL_LOOK="yaw,pitch" pins the view direction, so a scripted
        // verification run frames the same thing every time instead of whatever the mouse
        // happened to be doing.
        var lookOverride = Environment.GetEnvironmentVariable(LookOverrideVariable);
        if (!string.IsNullOrWhiteSpace(lookOverride))
        {
            if (TryParseLookOverride(lookOverride, out var look))
                return look;

            Log.WriteLine(
                $"Ignoring {LookOverrideVariable}=\"{lookOverride}\": expected two comma-separated numbers, e.g. -90,-20");
        }

        return DefaultLook;
    }

    private static bool TryParseLookOverride(string value, out Vector2 look)
    {
        look = DefaultLook;

        var parts = value.Split(',');
        if (parts.Length != 2)
            return false;

        if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var yaw)
            || !float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var pitch))
            return false;

        // Same clamp the mouse look uses, so an override can never start the view past vertical
        look = new Vector2(yaw, MathUtils.ClampPitch(pitch));
        return true;
    }

    /// <summary>
    /// Where a multiplayer client's camera stands until the server's spawn is known: a fixed
    /// point over the origin that reads nothing from worldgen (a client has no terrain of its own).
    /// </summary>
    internal static readonly Vector3 PlaceholderSpawn =
        new(0.5f, WorldGenerationConfig.SeaLevel + 1 + EyeHeight, 0.5f);

    /// <summary>
    /// Builds the camera. Headless tests pass nulls: no window to resize with and no mouse to
    /// capture. The game always passes both, so its behaviour is unchanged.
    /// </summary>
    /// <param name="window">The window to follow for the aspect ratio, or null headless.</param>
    /// <param name="input">The input context whose mouse turns the view, or null headless.</param>
    /// <param name="startPosition">
    /// The eye to start at; null searches the worldgen spawn (<c>VOXEL_SPAWN</c>, else the surface
    /// at the origin). A multiplayer client always passes one, so it never reads worldgen here.
    /// </param>
    public Camera(IWindow? window, IInputContext? input, Vector3? startPosition = null)
    {
        Position = startPosition ?? SpawnPosition();
        this.window = window;
        mouse = input?.Mice.FirstOrDefault();

        if (window != null)
        {
            _onResize = size => SetAspectRatio(size.X / (float)size.Y);
            window.FramebufferResize += _onResize;
        }

        if (mouse == null) return;
        mouse.Cursor.CursorMode = CursorMode.Disabled;
        mouse.MouseMove += OnMouseMove;
    }

    // Kept so Detach can unsubscribe exactly the handler the constructor added
    private readonly Action<Vector2D<int>>? _onResize;
    private bool _detached;

    /// <summary>
    /// Unhooks the camera from the window and mouse events the constructor subscribed to, so a
    /// torn-down world's camera stops turning its view and re-aspecting. Called by
    /// <c>World.SaveAndDispose</c>; idempotent. It does not change the cursor mode: whatever
    /// menu follows the world decides that.
    /// </summary>
    public void Detach()
    {
        if (_detached)
            return;
        _detached = true;
        if (window != null && _onResize != null)
            window.FramebufferResize -= _onResize;
        if (mouse != null)
            mouse.MouseMove -= OnMouseMove;
    }

    /// <summary>
    /// Save-restore seam: puts the player back at a saved eye position and facing with its
    /// saved modes. The pose goes through <see cref="TestTeleport"/>, so velocity, knockback and
    /// the shoulder state are zeroed exactly as for a scripted teleport. Flight implies creative,
    /// as <see cref="TestSetFlight"/> and the in-game toggle order already require.
    /// </summary>
    public void RestoreState(Vector3 position, float yaw, float pitch, bool creative, bool flying)
    {
        TestTeleport(position, yaw, pitch);
        IsCreative = creative || flying;
        IsFlying = flying;
    }

    /// <summary>
    /// Test seam: turns creative flight on or off directly, rather than scripting the C and
    /// double-Space keystrokes, so a visual scenario can hover over a spot instead of falling.
    /// Turning flight off leaves creative on, matching the in-game order.
    /// </summary>
    public void TestSetFlight(bool flying)
    {
        if (flying) IsCreative = true;
        IsFlying = flying;
    }

    /// <summary>
    /// Puts the modes where an authority (a multiplayer server refusing a creative or flight
    /// request) says they are, without moving the player. Flight implies creative; a change
    /// resets the vertical velocity exactly like the keyboard toggles do.
    /// </summary>
    public void SetModes(bool creative, bool flying)
    {
        creative |= flying;
        if (creative == IsCreative && flying == IsFlying)
            return;
        IsCreative = creative;
        IsFlying = flying;
        _physics.ResetVerticalVelocity();
    }

    /// <summary>
    /// Test seam: drops the eye at an exact spot facing an exact direction, so headless tests
    /// can aim the raycast and place the body without a window or input. Pitch is clamped
    /// exactly like the mouse look.
    /// </summary>
    public void TestTeleport(Vector3 position, float yaw, float pitch)
    {
        Position = position;
        Yaw = yaw;
        Pitch = MathUtils.ClampPitch(pitch);
        // A teleport is not a fall: without this a scenario dropped at altitude keeps the
        // speed it built up before the jump and plummets through the frame it wanted
        _physics.ResetMotion();
        _shoulderOffset = Vector3.Zero;
        _shoulderViewDir = Vector3.Zero;
        _shoulderBlendT = IsShoulderSurf ? 1f : 0f;
        _orbitPlayerYaw = yaw;
        _shoulderSide = Mode == CameraMode.Shoulder ? ShoulderSideOffset : 0f;
        _hasActionTarget = false;
    }

    /// <summary>
    /// Test seam: orbits the camera without touching the player body, simulating a mouse
    /// drag in orbit mode headlessly. Pitch is clamped exactly like the mouse look.
    /// </summary>
    public void TestSetCameraLook(float yaw, float pitch)
    {
        Yaw = yaw;
        Pitch = MathUtils.ClampPitch(pitch);
    }

    /// <summary>
    /// Sets the projection aspect ratio. The window's FramebufferResize handler calls this and
    /// nothing else, so a test exercising the real resize mutation path calls the same method
    /// the game does rather than mimicking it (PERF_TODO R7).
    /// </summary>
    internal void SetAspectRatio(float aspect) => _aspectRatio = aspect;

    /// <summary>Test seam: sets one view-state field in isolation, so a fact can prove that
    /// <see cref="GetViewProjection"/>'s cache key really contains it. Nothing in the game
    /// calls this; Update() owns all of these fields during play.</summary>
    internal void TestSetViewState(float? fov = null, float? roll = null, float? bobHeight = null,
        float? bobStrafe = null, Vector3? shoulderOffset = null, Vector3? shoulderViewDir = null,
        Vector3? shakeOffset = null, float? shakeRoll = null)
    {
        if (fov.HasValue) _currentFov = fov.Value;
        if (roll.HasValue) _roll = roll.Value;
        if (bobHeight.HasValue) _bobHeight = bobHeight.Value;
        if (bobStrafe.HasValue) _bobStrafe = bobStrafe.Value;
        if (shoulderOffset.HasValue) _shoulderOffset = shoulderOffset.Value;
        if (shoulderViewDir.HasValue) _shoulderViewDir = shoulderViewDir.Value;
        if (shakeOffset.HasValue) _physics.SetShakeOffset(shakeOffset.Value);
        if (shakeRoll.HasValue) _physics.SetShakeRoll(shakeRoll.Value);
    }

    /// <summary>
    /// Tells orbit mode the player just interacted (mine/place/use click): the body turns
    /// toward the camera aim over the next frames while the action itself fires at once.
    /// No-op outside orbit mode, where the body already faces the camera.
    /// </summary>
    public void NotifyPlayerAction()
    {
        if (!IsOrbit) return;
        _actionYaw = Yaw;
        _hasActionTarget = true;
    }

    /// <summary>
    /// Kicks the damage shake: <paramref name="amount"/> adds to trauma (0..1, clamped),
    /// which <see cref="Update"/> decays back to still over <c>ShakeDecaySeconds</c>.
    /// Callers scale it by the hit — World bills fall and lava ticks through here.
    /// </summary>
    public void AddShake(float amount)
    {
        if (amount <= 0f) return;
        _physics.AddShake(amount);
    }

    /// <summary>
    /// Knocks the player back from a mob hit: a horizontal shove that decays over a few tenths of
    /// a second — the normal collision still stops it, so a hit can never push the player through
    /// a wall — plus a small upward pop. Flight ignores both (and cannot be hit).
    /// </summary>
    /// While set, mouse movement no longer turns the view (the [E] panel owns the cursor).
    /// Position still updates so reopening the game never jumps the look.
    /// </summary>
    public bool SuppressLook { get; set; }

    private void OnMouseMove(IMouse mouse, Vector2 position)
    {
        if (SuppressLook)
        {
            _lastMousePosition = position;
            _firstMouseMovement = false;
            return;
        }
        if (_firstMouseMovement)
        {
            _lastMousePosition = position;
            _firstMouseMovement = false;
            return;
        }

        var delta = position - _lastMousePosition;
        _lastMousePosition  = position;

        Yaw += delta.X * MouseSensitivity;
        Pitch -= delta.Y * MouseSensitivity;

        Pitch = MathUtils.ClampPitch(Pitch);
    }

    /// <summary>
    /// The per-frame view juice: FOV (sprint widening plus punch pinch), head bob, landing dip,
    /// crouch drop and roll. <paramref name="sprintHeld"/> is the sprint key, on foot or flying;
    /// <see cref="SprintFov"/> decides whether that actually widens the view.
    /// <paramref name="strafe"/> is the sideways movement input along the camera's right (-1..1),
    /// which <see cref="StrafeTilt"/> turns into a lean.
    /// </summary>
    internal void UpdateHeadMotion(float deltaTime, float intendedSpeed, bool moving, bool swimming,
        bool sprintHeld = false, float strafe = 0f)
    {
        _sprintFov = SprintFov.Step(_sprintFov,
            SprintFov.TargetDegrees(sprintHeld, moving, IsSneaking, swimming, intendedSpeed), deltaTime);
        _fovPunch.Step(0f, deltaTime);
        _currentFov = DefaultFov + _sprintFov + MathUtils.Clamp(_fovPunch.Value, -6f, 6f);

        // A landing dips the view in proportion to the fall that ended just now.
        if (!_wasGrounded && IsGrounded && !IsFlying && _prevVerticalVelocity < -4f)
            _landDip.Impulse(MathF.Min(-_prevVerticalVelocity, 25f) * LandDipPerSpeed);
        _wasGrounded = IsGrounded;
        _prevVerticalVelocity = _physics.VerticalVelocity;
        _landDip.Step(0f, deltaTime);
        _kickRoll.Step(0f, deltaTime);
        _sneakBlend = MathUtils.Damp(_sneakBlend, IsSneaking ? 1f : 0f, 12f, deltaTime);

        _runBlend = MathUtils.Damp(_runBlend, IsSprinting ? 1f : 0f, RunBlendRate, deltaTime);
        var bobTarget = moving && !IsFlying && (IsGrounded || swimming) ? 1f : 0f;
        _bobBlend = MathUtils.Damp(_bobBlend, bobTarget, BobDampRate, deltaTime);
        if (moving && !IsFlying)
            _bobPhase += deltaTime * (4f + intendedSpeed * 0.9f + _runBlend * 2.5f);

        var height = MathUtils.Lerp(WalkBobHeight, RunBobHeight, _runBlend);
        var bobStrafe = MathUtils.Lerp(WalkBobStrafe, RunBobStrafe, _runBlend);
        var death = MathUtils.EaseInCubic(_deathProgress);
        _bobHeight = MathF.Sin(_bobPhase * 2f) * height * _bobBlend
                     - MathUtils.Clamp(_landDip.Value, -4f, 12f) * LandDipScale
                     - SneakEyeDrop * _sneakBlend - DeathDropBlocks * death;
        _bobStrafe = MathF.Cos(_bobPhase) * bobStrafe * _bobBlend;
        _strafeTilt = StrafeTilt.Step(_strafeTilt,
            StrafeTilt.TargetRadians(strafe, IsFlying, dead: _deathProgress > 0f), deltaTime);
        _roll = MathF.Sin(_bobPhase) * RunRoll * _runBlend * _bobBlend
                + MathUtils.Clamp(_kickRoll.Value, -0.12f, 0.12f) + DeathRollRadians * death + _strafeTilt;
    }

    /// <summary>
    /// Called instead of the toggle updates while a text field owns the keyboard: the edge
    /// latches read as held, so a C, Space or F5 typed into the field and still down when the
    /// field lets go is not taken for a fresh press on the first free frame.
    /// </summary>
    internal void MarkTogglesHeld()
    {
        _creativeWasPressed = true;
        _spaceWasPressed = true;
        _f5WasPressed = true;
    }

    internal void UpdateCreativeToggle(IKeyboard keyboard)
    {
        var creativePressed = keyboard.IsKeyPressed(Key.C);
        if (creativePressed && !_creativeWasPressed)
        {
            IsCreative = !IsCreative;
            if (!IsCreative)
                IsFlying = false;
            _physics.ResetVerticalVelocity();
            Log.WriteLine($"Creative {(IsCreative ? "ON" : "OFF")}");
        }

        _creativeWasPressed = creativePressed;
    }

    internal void UpdateFlightToggle(float deltaTime, IKeyboard keyboard)
    {
        _timeSinceSpacePress += deltaTime;

        var spacePressed = keyboard.IsKeyPressed(Key.Space);
        if (spacePressed && !_spaceWasPressed)
        {
            if (_timeSinceSpacePress <= DoubleTapWindow)
            {
                // Flight is a creative-only move: on foot the second tap is just a jump.
                if (IsCreative)
                {
                    IsFlying = !IsFlying;
                    _physics.ResetVerticalVelocity();
                    Log.WriteLine($"Flight {(IsFlying ? "ON" : "OFF")}");
                }
                _timeSinceSpacePress = float.MaxValue; // a toggle requires a fresh double tap
            }
            else
            {
                _timeSinceSpacePress = 0f;
            }
        }

        _spaceWasPressed = spacePressed;
    }

    internal void UpdateShoulderToggle(IKeyboard keyboard)
    {
        var pressed = keyboard.IsKeyPressed(Key.F5);
        if (pressed && !_f5WasPressed)
        {
            Mode = (CameraMode)(((int)Mode + 1) % 3);
            if (Mode == CameraMode.Orbit)
            {
                _orbitPlayerYaw = Yaw;
                _hasActionTarget = false;
            }
            Log.WriteLine($"Camera {Mode}");
        }

        _f5WasPressed = pressed;
    }

    internal void UpdateShoulderOffset(ChunkManager chunkManager, float deltaTime)
    {
        var forward = GetForwardVector();
        if (!IsShoulderSurf && _shoulderBlendT <= 0f)
        {
            _shoulderOffset = Vector3.Zero;
            _shoulderViewDir = forward;
            _shoulderSide = Mode == CameraMode.Shoulder ? ShoulderSideOffset : 0f;
            return;
        }

        _shoulderSide = MathUtils.Damp(_shoulderSide,
            Mode == CameraMode.Shoulder ? ShoulderSideOffset : 0f, OrbitSideRate, deltaTime);
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var desired = -forward * ShoulderBackDist + Vector3.UnitY * ShoulderHeightOffset + right * _shoulderSide;
        var length = desired.Length();
        if (length < float.Epsilon)
        {
            _shoulderOffset = Vector3.Zero;
            _shoulderViewDir = forward;
            return;
        }

        var steps = Math.Max(1, (int)MathF.Ceiling(length / ShoulderProbeStep));
        var resolved = desired;
        for (var i = 1; i <= steps; i++)
        {
            var probe = Position + desired * (i / (float)steps);
            if (!IsCameraBlocked(probe, chunkManager))
                continue;

            var safeT = Math.Max(0f, (i - 1) / (float)steps - ShoulderPullMargin / length);
            resolved = desired * safeT;
            break;
        }

        // The glide rides the blend: at 0 the camera sits in the head no matter what the
        // march found, so toggling out slides back along the collision-free line.
        var effective = resolved * ShoulderBlend;
        _shoulderOffset = effective;
        var viewDir = forward * ShoulderAimDistance - effective;
        _shoulderViewDir = viewDir.NormalizeSafeOr(forward, 1e-8f);
    }

    private static bool IsCameraBlocked(Vector3 point, ChunkManager chunkManager)
    {
        var (pointX, pointY, pointZ) = point.BlockCell();
        var id = chunkManager.GetBlockGlobal(pointX, pointY, pointZ);
        return BlockType.IsSolid(id) && BlockType.MovementSpeedOf(id) <= 0f;
    }

    // Refreshes EyeWaterSurfaceY while the eye is in water (not lava, which draws no rays);
    // a non-finite result (the eye cell stopped being loaded fluid) keeps the last value
    internal void UpdateEyeWaterSurface(ChunkManager chunkManager, int eyeX, int eyeY, int eyeZ)
    {
        if (!IsEyeInWater || IsEyeInLava)
            return;

        var surface = chunkManager.GetFluidColumnTopGlobal(eyeX, eyeY, eyeZ);
        if (float.IsFinite(surface))
            EyeWaterSurfaceY = surface;
    }

    internal void RefreshEyeWaterState(ChunkManager chunkManager)
    {
        if (!IsShoulderSurf)
            return;

        var eye = Position + _shoulderOffset;
        var (eyeX, eyeY, eyeZ) = eye.BlockCell();
        IsEyeInWater = eye.Y < chunkManager.GetFluidSurfaceGlobal(eyeX, eyeY, eyeZ);
        IsEyeInLava = false;
        if (IsEyeInWater && chunkManager.TryGetCell(eyeX, eyeY, eyeZ, out var eyeId, out _)
            && BlockType.IsFluid(eyeId))
        {
            IsEyeInLava = BlockType.Emission[eyeId] > 0;
        }

        UpdateEyeWaterSurface(chunkManager, eyeX, eyeY, eyeZ);
    }

    /// <summary>
    /// Auto-step: replays a blocked horizontal move from raised copies of <paramref name="start"/>,
    /// trying the smallest lift first, and returns the first one that clears the ledge and travels
    /// the full frame movement. Returns null when the obstacle is taller than <paramref name="stepHeight"/>
    /// (or the head is boxed in), leaving the caller's normally blocked position in place.
    /// </summary>
    /// </summary>
    /// </summary>
    /// against the body does not count, exactly like <see cref="OverlapsPlayer"/>.
    /// </summary>

    /// <summary>Runs the per-frame movement simulation (see <see cref="PlayerPhysics"/>).</summary>
    public void Update(float deltaTime, IKeyboard? keyboard, ChunkManager chunkManager) =>
        _physics.Update(this, deltaTime, keyboard, chunkManager);

    /// <summary>Test seam for the physics state; the game reaches these through PlayerPhysics.</summary>
    internal void SetPosition(Vector3 position) => Position = position;
    internal void MoveBy(Vector3 delta) => Position += delta;
    internal void SetGrounded(bool grounded) => IsGrounded = grounded;
    internal void SetSprinting(bool sprinting) => IsSprinting = sprinting;
    internal void SetSneaking(bool sneaking) => IsSneaking = sneaking;

    /// <summary>Scenario seam: selects the camera mode directly (the F5 cycle without the key).</summary>
    internal void TestSetMode(CameraMode mode)
    {
        Mode = mode;
        _shoulderBlendT = IsShoulderSurf ? 1f : 0f;
        if (mode == CameraMode.Orbit)
        {
            _orbitPlayerYaw = Yaw;
            _hasActionTarget = false;
        }
    }
    internal void SetMovementFactor(float factor) => MovementFactor = factor;
    internal void SetGroundFriction(float friction) => GroundFriction = friction;
    internal void SetInWater(bool inWater) => IsInWater = inWater;
    internal void SetEyeInWater(bool inWater) => IsEyeInWater = inWater;
    internal void SetInLava(bool inLava) => IsInLava = inLava;
    internal void SetEyeInLava(bool inLava) => IsEyeInLava = inLava;
    internal void SetShoulderBlendT(float t) => _shoulderBlendT = t;
    internal float ShoulderBlendT => _shoulderBlendT;
    internal void SetOrbitPlayerYaw(float yaw) => _orbitPlayerYaw = yaw;
    internal float OrbitPlayerYaw => _orbitPlayerYaw;
    internal bool HasActionTarget => _hasActionTarget;
    internal float ActionYaw => _actionYaw;
    internal void ClearActionTarget() => _hasActionTarget = false;

    /// <summary>Ground acceleration response for a top-face friction, 0..1 (see <see cref="PlayerPhysics"/>).</summary>
    internal static float GroundAccelRate(float friction) => PlayerPhysics.GroundAccelRate(friction);

    /// <summary>
    /// Knocks the player back from a mob hit: a horizontal shove that decays over a few tenths of
    /// a second — the normal collision still stops it, so a hit can never push the player through
    /// a wall — plus a small upward pop. Flight ignores both (and cannot be hit).
    /// </summary>
    public void AddKnockback(Vector3 horizontalDirection, float horizontalSpeed, float lift) =>
        _physics.AddKnockback(this, horizontalDirection, horizontalSpeed, lift);

    /// <summary>
    /// Whether a block cell at <paramref name="blockPos"/> overlaps the player's body. The cell
    /// spans one full cube from its floored corner; touching a face exactly does not count, so
    /// placing a block under the feet or beside the body stays legal.
    /// </summary>
    public bool OverlapsPlayer(Vector3 blockPos) => PlayerContext.BodyOverlapsCell(Position, blockPos);

    /// <summary>
    /// Shoves the player out of a falling cube, along the smallest-penetration axis. Strictly
    /// one-way: cubes push the player, the player never pushes cubes. Axes that would park
    /// the eye inside a solid block are skipped, so a cube cannot shove the player through a
    /// wall; flight ignores shoves entirely.
    /// </summary>
    public void PushOutOf(Vector3 center, float halfExtent, ChunkManager chunkManager)
    {
        if (IsFlying) return;

        var min = new Vector3(
            Position.X - PlayerPhysics.PlayerRadius,
            Position.Y - EyeHeight,
            Position.Z - PlayerPhysics.PlayerRadius);
        var max = new Vector3(
            Position.X + PlayerPhysics.PlayerRadius,
            Position.Y - EyeHeight + PlayerPhysics.PlayerHeight,
            Position.Z + PlayerPhysics.PlayerRadius);
        var eMin = center - new Vector3(halfExtent);
        var eMax = center + new Vector3(halfExtent);

        var ox = Math.Min(max.X, eMax.X) - Math.Max(min.X, eMin.X);
        var oy = Math.Min(max.Y, eMax.Y) - Math.Max(min.Y, eMin.Y);
        var oz = Math.Min(max.Z, eMax.Z) - Math.Max(min.Z, eMin.Z);
        if (ox <= 0f || oy <= 0f || oz <= 0f) return;

        // Signed shove per axis that moves the body fully clear of the cube; ties go negative.
        // Shallowest first, so the shove moves the player the least.
        var bodyCenterY = Position.Y - EyeHeight + PlayerPhysics.PlayerHeight * 0.5f;
        Span<float> pushes = [
            Position.X <= center.X ? eMin.X - max.X : eMax.X - min.X,
            bodyCenterY <= center.Y ? eMin.Y - max.Y : eMax.Y - min.Y,
            Position.Z <= center.Z ? eMin.Z - max.Z : eMax.Z - min.Z,
        ];
        Span<int> order = [0, 1, 2];
        for (var a = 0; a < 2; a++)
        for (var b = a + 1; b < 3; b++)
            if (Math.Abs(pushes[order[b]]) < Math.Abs(pushes[order[a]]))
                (order[a], order[b]) = (order[b], order[a]);

        foreach (var axis in order)
        {
            var candidate = axis switch
            {
                0 => Position + new Vector3(pushes[0], 0f, 0f),
                1 => Position + new Vector3(0f, pushes[1], 0f),
                _ => Position + new Vector3(0f, 0f, pushes[2]),
            };

            if (!_physics.IsColliding(candidate, chunkManager))
            {
                Position = candidate;
                return;
            }
        }
    }

    /// <summary>
    /// Whether an entity cube at <paramref name="center"/> with the given half extent touches
    /// the player's body box. Bodies only touch when they genuinely overlap: a face flush
    /// against the body does not count, exactly like <see cref="OverlapsPlayer"/>.
    /// </summary>
    public bool BodyTouches(Vector3 center, float halfExtent) =>
        MobTargets.BodyTouches(Position, center, halfExtent);

    /// <inheritdoc />
    Vector3 IMobTarget.Eye => Position;

    /// <inheritdoc />
    public bool IsImmune => IsCreative || IsFlying;

    /// <summary>
    /// A mob hit on the local player: only the knockback is applied here. The
    /// <paramref name="damage"/> is deliberately ignored, because <see cref="World"/> applies the
    /// mob manager's <c>DamageDealtThisFrame</c> to its own health pool (and shakes the view)
    /// exactly as before mobs had targets.
    /// </summary>
    public void TakeHit(int damage, Vector3 awayHorizontal, float knockbackSpeed, float lift) =>
        AddKnockback(awayHorizontal, knockbackSpeed, lift);

    public Matrix4x4 GetViewMatrix()
    {
        var viewDir = ViewDirection;
        var right = Vector3.Normalize(Vector3.Cross(viewDir, Vector3.UnitY));
        var up = Vector3.Transform(Vector3.UnitY,
            Matrix4x4.CreateFromAxisAngle(viewDir, _roll + _physics.ShakeRoll));
        var eye = Position + Vector3.UnitY * _bobHeight + right * _bobStrafe + _shoulderOffset + _physics.ShakeOffset;
        return Matrix4x4.CreateLookAt(eye, eye + viewDir, up);
    }

    public Matrix4x4 GetProjectionMatrix() =>
        Matrix4x4.CreatePerspectiveFieldOfView(MathUtils.ToRadians(_currentFov), _aspectRatio, NearPlane, FarPlane);

    /// <summary>
    /// View * projection, cached per distinct camera pose (PERF_TODO R7). Twenty-two call
    /// sites a frame used to rebuild both matrices; the cache is keyed on
    /// <see cref="ViewProjKey"/> and compared bit-for-bit, so a hit only ever returns the
    /// matrix the recomputation would have produced.
    ///
    /// The invariant: the key holds EVERY field <see cref="GetViewMatrix"/> and
    /// <see cref="GetProjectionMatrix"/> read. Adding a field to either builder without
    /// adding it here is the one way this goes wrong.
    /// <c>CameraViewProjectionCacheTests</c> carries one fact per key field to catch a field
    /// being dropped from the key, and <c>KeyHoldsExactlyTheMatrixInputs</c> makes editing
    /// the key a conscious act. <see cref="GetViewMatrix"/> and
    /// <see cref="GetProjectionMatrix"/> stay deliberately uncached: they are the mirror
    /// pass's standalone answer and the tests' oracle.
    ///
    /// <see cref="GetViewFrustum"/> shares this one key and is refreshed in the same place,
    /// so the two can never drift apart.
    /// </summary>
    public Matrix4x4 GetViewProjection()
    {
        EnsureViewCache();
        return _viewProj;
    }

    /// <summary>
    /// The view frustum of <see cref="GetViewProjection"/>, built once per distinct camera
    /// pose (PERF_TODO R11). Three passes - RenderWorld, CullWater and RenderLod - used to
    /// derive this independently from the identical unchanged matrix, paying six plane
    /// extractions and normalisations three times a frame. The shadow and mirror frusta are
    /// built from the shadow cascades' <c>ViewProj</c> / <c>_reflViewProj</c> and are deliberately not
    /// routed through here.
    /// </summary>
    public Frustum GetViewFrustum()
    {
        EnsureViewCache();
        return _viewFrustum;
    }

    /// <summary>
    /// Revalidates the cached view-projection matrix and its frustum against the current
    /// pose. One key and one miss branch for both, so a caller can never see a frustum that
    /// belongs to a different matrix than <see cref="GetViewProjection"/> returns; rebuilding
    /// the frustum on a matrix miss is free relative to the matrix build itself.
    /// </summary>
    private void EnsureViewCache()
    {
        var key = CurrentViewProjKey();
        if (_vpValid && SameBits(in _vpKey, in key)) return;

        _vpKey = key;
        _viewProj = GetViewMatrix() * GetProjectionMatrix();
        _viewFrustum = new Frustum(_viewProj);
        _vpValid = true;
    }

    /// <summary>
    /// Exactly the mutable state the two matrix builders read. <see cref="GetViewMatrix"/>
    /// reads <see cref="ViewDirection"/> (<c>_shoulderViewDir</c> when non-degenerate, else
    /// <see cref="GetForwardVector"/> -> <see cref="Yaw"/>/<see cref="Pitch"/>),
    /// <c>_roll</c>, <c>_shakeRoll</c>, <see cref="Position"/>, <c>_bobHeight</c>,
    /// <c>_bobStrafe</c>, <c>_shoulderOffset</c> and <c>_shakeOffset</c>;
    /// <see cref="GetProjectionMatrix"/> reads <c>_currentFov</c>
    /// and <c>_aspectRatio</c>. <c>NearPlane</c>/<c>FarPlane</c> are <c>const</c> and so are
    /// deliberately not in the key.
    /// </summary>
    private ViewProjKey CurrentViewProjKey() =>
        new(Position, _shoulderViewDir, _shoulderOffset, Yaw, Pitch, _roll, _bobHeight, _bobStrafe,
            _physics.ShakeOffset, _physics.ShakeRoll, _currentFov, _aspectRatio);

    /// <summary>
    /// Bit-for-bit key comparison. Deliberately not <c>==</c> or the record's generated
    /// equality: <c>float</c> equality calls +0f and -0f equal and the generated
    /// <c>Equals</c> calls NaN and NaN equal, and the correctness argument here is strictly
    /// "the inputs were bit-identical, therefore the recomputation would have been
    /// bit-identical".
    /// </summary>
    private static bool SameBits(in ViewProjKey a, in ViewProjKey b) =>
        SameBits(a.Position, b.Position)
        && SameBits(a.ShoulderViewDir, b.ShoulderViewDir)
        && SameBits(a.ShoulderOffset, b.ShoulderOffset)
        && SameBits(a.Yaw, b.Yaw)
        && SameBits(a.Pitch, b.Pitch)
        && SameBits(a.Roll, b.Roll)
        && SameBits(a.BobHeight, b.BobHeight)
        && SameBits(a.BobStrafe, b.BobStrafe)
        && SameBits(a.ShakeOffset, b.ShakeOffset)
        && SameBits(a.ShakeRoll, b.ShakeRoll)
        && SameBits(a.Fov, b.Fov)
        && SameBits(a.Aspect, b.Aspect);

    private static bool SameBits(float a, float b) =>
        BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

    private static bool SameBits(in Vector3 a, in Vector3 b) =>
        SameBits(a.X, b.X) && SameBits(a.Y, b.Y) && SameBits(a.Z, b.Z);

    /// <summary>
    /// The inputs of <see cref="GetViewProjection"/>'s cache, and nothing else. Adding a
    /// member here means the matrix builders gained an input; removing one means they lost
    /// it. See the invariant on <see cref="GetViewProjection"/>.
    /// </summary>
    private readonly record struct ViewProjKey(
        Vector3 Position,
        Vector3 ShoulderViewDir,
        Vector3 ShoulderOffset,
        float Yaw,
        float Pitch,
        float Roll,
        float BobHeight,
        float BobStrafe,
        Vector3 ShakeOffset,
        float ShakeRoll,
        float Fov,
        float Aspect);

    /// <summary>View direction, for aiming the shadow camera ahead of the player.</summary>
    public Vector3 Forward => GetForwardVector();

    internal Vector3 GetForwardVector() => Vector3Utils.FromYawPitch(Yaw, Pitch);

    /// <summary>
    /// Walks the view ray cell by cell (DDA) and reports the first cell it can target. The ray
    /// leaves the head in first person and the shoulder camera in third person, along
    /// <see cref="ViewDirection"/>, so the highlight always lands under the crosshair. In
    /// third person the head then re-casts toward the cursor target under first-person
    /// rules and must reach the same cell, so range and line-of-sight never exceed first
    /// person. Fluids are transparent to targeting unless <paramref name="includeFluids"/>
    /// is set, which only an item that works on them (the bucket) asks for. A cell only
    /// counts when the ray enters one of the block's hitboxes (see <see cref="BlockHitbox"/>),
    /// so small blocks like flowers are aimed through everywhere outside their shape.
    /// </summary>
    public bool Raycast(float maxDistance, ChunkManager chunkManager, out Vector3 hitPosition, out Vector3 hitNormal,
        bool includeFluids = false) =>
        Raycast(maxDistance, chunkManager, out hitPosition, out hitNormal, out _, includeFluids);

    /// <summary>
    /// Box-aware raycast: like <see cref="Raycast(float, ChunkManager, out Vector3, out Vector3, bool)"/>,
    /// but also reports which of the hit block's hitboxes was entered, so the selection outline
    /// can highlight that box's face.
    /// </summary>
    public bool Raycast(float maxDistance, ChunkManager chunkManager, out Vector3 hitPosition, out Vector3 hitNormal,
        out int hitBoxIndex, bool includeFluids = false)
    {
        hitPosition = Vector3.Zero;
        hitNormal = Vector3.Zero;
        hitBoxIndex = 0;

        if (!IsShoulderSurf)
        {
            LastAimDirection = GetForwardVector();
            return BlockRaycaster.Raycast(Position, LastAimDirection, maxDistance, 0f, stopAtRange: false,
                chunkManager, out hitPosition, out hitNormal, out hitBoxIndex, out _, includeFluids);
        }

        // Crosshair target from the shoulder camera. Its walk is longer than head reach
        // because it starts behind the head; the head check below cuts it back down.
        LastAimDirection = ViewDirection;
        var camRange = maxDistance + _shoulderOffset.Length();
        if (!BlockRaycaster.Raycast(RenderEye, ViewDirection, camRange, camRange * 3 + 2, stopAtRange: true,
                chunkManager, out var camHit, out var camNormal, out var camBox, out var camT, includeFluids))
            return false;

        // Head validation: the head re-casts toward the exact surface point the camera
        // hit, under first-person rules, and must arrive at the same cell. Range and
        // line-of-sight therefore never exceed first person: nothing is mined or placed
        // through walls or past what the head could hit itself.
        var surface = RenderEye + ViewDirection * camT;
        var toSurface = surface - Position;
        var headDist = toSurface.Length();
        if (headDist < 1e-4f)
        {
            (hitPosition, hitNormal, hitBoxIndex) = (camHit, camNormal, camBox);
            return true;
        }
        LastAimDirection = toSurface / headDist;
        if (!BlockRaycaster.Raycast(Position, LastAimDirection, maxDistance, 0f, stopAtRange: false,
                chunkManager, out var headHit, out _, out _, out _, includeFluids))
            return false;
        if (headHit != camHit)
            return false;

        (hitPosition, hitNormal, hitBoxIndex) = (camHit, camNormal, camBox);
        return true;
    }

}
