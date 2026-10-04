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

public class Camera
{
    public Vector3 Position { get; private set; } = SpawnPosition();
    public float Yaw { get; private set; } = SpawnLook.X;
    public float Pitch { get; private set; } = SpawnLook.Y;
    public bool IsGrounded { get; private set; }
    public bool IsFlying { get; private set; }
    /// <summary>True while sprinting on foot with movement input; drives the run cycle, roll and FOV.</summary>
    public bool IsSprinting { get; private set; }
    /// <summary>Current vertical FOV in degrees; eases toward sprint boost with speed.</summary>
    public float CurrentFov => _currentFov;
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
    [Obsolete("Renamed to IsCreative.")]
    public bool IsNoclip => IsCreative;
    public float VerticalVelocity => _verticalVelocity;

    /// <summary>
    /// Movement-speed multiplier from the pass-through blocks the body overlaps this frame: the
    /// smallest block <see cref="BlockType.MovementSpeed"/>, or 1 when none. Flight still reports
    /// the sampled value even though it does not apply it, so the debug line can tell a flyer from
    /// a walker standing in a block with no movement speed.
    /// </summary>
    public float MovementFactor { get; private set; } = 1f;
    public float GroundFriction { get; private set; } = BlockType.DefaultFriction;
    public Vector2 HorizontalVelocity => _horizontalVelocity;

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

    private const float SprintFovBoost = 5f;
    private const float FovDampRate = 6f;
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
    private const float ShoulderTransitionSeconds = 0.5f;
    // Orbit player turn speed, in exponential-damp units per second: fast but visibly lerped.
    private const float OrbitTurnRate = 10f;
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
    private float _verticalVelocity;
    private Vector2 _lastMousePosition;
    private bool _firstMouseMovement = true;
    private bool _spaceWasPressed;
    private bool _creativeWasPressed;
    private float _timeSinceSpacePress = float.MaxValue;
    // Averaged flow of the fluid cells the body is in, refreshed by UpdateWaterState
    private Vector3 _waterFlow;

    // Decaying horizontal shove from a mob hit, integrated into the walk each frame
    private Vector3 _knockback;
    private Vector2 _horizontalVelocity;

    // Damage shake: trauma 0..1, fed by AddShake and decayed in Update. The view offset
    // and roll ride trauma-squared, so small hits tremble and big hits lurch.
    private float _trauma;
    private float _shakeTime;
    private Vector3 _shakeOffset = Vector3.Zero;
    private float _shakeRoll;
    private const float ShakeDecaySeconds = 0.7f;
    private const float ShakeMaxOffset = 0.3f;
    private const float ShakeMaxRoll = 0.04f;

    private readonly IWindow? window;
    private readonly IMouse? mouse;

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
        var surface = Math.Max(Chunk.GetColumnHeights(0, 0)[0], WorldGenerationConfig.SeaLevel) + 1;
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

    // Headless tests pass nulls: no window to resize with and no mouse to capture.
    // The game always passes both, so its behaviour is unchanged.
    public Camera(IWindow? window, IInputContext? input)
    {
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
        _verticalVelocity = 0f;
        _knockback = Vector3.Zero;
        _horizontalVelocity = Vector2.Zero;
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
        if (shakeOffset.HasValue) _shakeOffset = shakeOffset.Value;
        if (shakeRoll.HasValue) _shakeRoll = shakeRoll.Value;
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
        _trauma = Math.Min(1f, _trauma + amount);
    }

    /// <summary>
    /// Knocks the player back from a mob hit: a horizontal shove that decays over a few tenths of
    /// a second — the normal collision still stops it, so a hit can never push the player through
    /// a wall — plus a small upward pop. Flight ignores both (and cannot be hit).
    /// </summary>
    public void AddKnockback(Vector3 horizontalDirection, float horizontalSpeed, float lift)
    {
        horizontalDirection.Y = 0f;
        if (horizontalDirection.LengthSquared() > 1e-6f)
        {
            horizontalDirection = Vector3.Normalize(horizontalDirection);
            _knockback.X = horizontalDirection.X * horizontalSpeed;
            _knockback.Z = horizontalDirection.Z * horizontalSpeed;
        }
        if (!IsFlying && lift > 0f)
            _verticalVelocity = MathF.Max(_verticalVelocity, lift);
    }

    private void UpdateShake(float deltaTime)
    {
        _shakeTime += Math.Max(0f, deltaTime);
        if (_trauma <= 0f)
        {
            _shakeOffset = Vector3.Zero;
            _shakeRoll = 0f;
            return;
        }

        _trauma = Math.Max(0f, _trauma - Math.Max(0f, deltaTime) / ShakeDecaySeconds);
        // Squared falloff plus mismatched sines per axis, so the view shudders instead of
        // swaying on one clean frequency.
        var t = _trauma * _trauma;
        var time = _shakeTime;
        _shakeOffset = new Vector3(
            MathF.Sin(time * 39.7f) * ShakeMaxOffset * t,
            MathF.Sin(time * 44.3f + 1.3f) * ShakeMaxOffset * 0.6f * t,
            MathF.Sin(time * 31.9f + 2.1f) * ShakeMaxOffset * t);
        _shakeRoll = MathF.Sin(time * 47.7f + 0.7f) * ShakeMaxRoll * t;
    }

    /// <summary>
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

    public void Update(float deltaTime, IKeyboard? keyboard, ChunkManager chunkManager)
    {
        if (keyboard == null || chunkManager == null)
            return;

        UpdateCreativeToggle(keyboard);
        UpdateFlightToggle(deltaTime, keyboard);
        UpdateShoulderToggle(keyboard);
        _shoulderBlendT = Math.Clamp(_shoulderBlendT
            + (IsShoulderSurf ? deltaTime : -deltaTime) / ShoulderTransitionSeconds, 0f, 1f);
        UpdateWaterState(chunkManager);
        _knockback.X = MathUtils.Damp(_knockback.X, 0f, KnockbackDamping, deltaTime);
        _knockback.Z = MathUtils.Damp(_knockback.Z, 0f, KnockbackDamping, deltaTime);

        var sprinting = keyboard.IsKeyPressed(Key.ShiftLeft);
        var moveSpeed = BaseSpeed * (sprinting ? (IsFlying ? FlyBoostMultiplier : SprintMultiplier) : 1f);
        var direction = IsOrbit ? ViewDirection : GetForwardVector();
        var right = Vector3.Normalize(Vector3.Cross(direction, Vector3.UnitY));
        var forward = Vector3.Normalize(new Vector3(direction.X, 0f, direction.Z));

        Vector3 moveInput = Vector3.Zero;
        if (keyboard.IsKeyPressed(Key.W)) moveInput += forward;
        if (keyboard.IsKeyPressed(Key.S)) moveInput -= forward;
        if (keyboard.IsKeyPressed(Key.A)) moveInput -= right;
        if (keyboard.IsKeyPressed(Key.D)) moveInput += right;

        if (moveInput.LengthSquared() > 1f)
            moveInput = Vector3.Normalize(moveInput);

        // Orbit faces its motion, so it is always walking forward, never backing up.
        var backing = !IsOrbit && keyboard.IsKeyPressed(Key.S) && !keyboard.IsKeyPressed(Key.W);
        if (backing && !IsFlying) moveSpeed *= 0.3f;

        // In orbit the body turns toward the motion (or, after a click, toward the aim)
        // while the camera orbits freely; everywhere else it already faces the camera.
        var moving = moveInput.LengthSquared() > 0.01f;
        if (IsOrbit)
        {
            if (moving)
            {
                var motionYaw = MathUtils.ToDegrees(MathF.Atan2(moveInput.Z, moveInput.X));
                _orbitPlayerYaw = MathUtils.DampAngle(_orbitPlayerYaw, motionYaw, OrbitTurnRate, deltaTime);
            }
            else if (_hasActionTarget)
            {
                _orbitPlayerYaw = MathUtils.DampAngle(_orbitPlayerYaw, _actionYaw, OrbitTurnRate, deltaTime);
                var remaining = _actionYaw - _orbitPlayerYaw;
                remaining -= MathF.Round(remaining / 360f) * 360f;
                if (Math.Abs(remaining) < 0.5f)
                    _hasActionTarget = false;
            }
        }
        else
        {
            _orbitPlayerYaw = Yaw;
        }

        // Flight ignores the water entirely, so a submerged player can still fly out.
        // Creative mode on foot swims like survival.
        var swimming = IsInWater && !IsFlying;
        IsSprinting = sprinting && moving && !IsFlying && !swimming && IsGrounded;
        if (swimming) moveSpeed *= SwimSpeedFactor;
        // Pass-through blocks scale the walk instead of blocking it; flight is noclip.
        MovementFactor = BlockMoveFactor(chunkManager);
        if (!IsFlying) moveSpeed *= MovementFactor;
        var wish = new Vector2(moveInput.X * moveSpeed, moveInput.Z * moveSpeed);
        float accelRate;
        if (IsFlying)
            accelRate = FlyAccelRate;
        else if (swimming)
            accelRate = SwimAccelRate;
        else if (IsGrounded)
        {
            GroundFriction = SampleGroundFriction(chunkManager);
            accelRate = GroundAccelRate(GroundFriction);
        }
        else
        {
            accelRate = AirAccelRate;
        }
        _horizontalVelocity.X = MathUtils.Damp(_horizontalVelocity.X, wish.X, accelRate, deltaTime);
        _horizontalVelocity.Y = MathUtils.Damp(_horizontalVelocity.Y, wish.Y, accelRate, deltaTime);

        var intendedSpeed = _horizontalVelocity.Length();
        UpdateHeadMotion(deltaTime, intendedSpeed, moving, swimming);
        UpdateShake(deltaTime);

        var frameMovement = new Vector3(_horizontalVelocity.X * deltaTime, 0f, _horizontalVelocity.Y * deltaTime);

        // Mob knockback rides the walk like the water flow below: a decaying horizontal shove that
        // the same axis moves still stop against a wall. Flight clears it (it cannot be hit).
        if (IsFlying)
        {
            _knockback = Vector3.Zero;
        }
        else
        {
            frameMovement.X += _knockback.X * deltaTime;
            frameMovement.Z += _knockback.Z * deltaTime;
        }

        // Flowing water carries the player with it. Added before the horizontal moves below, so
        // the push is still stopped by a wall rather than shoving the player through it.
        if (swimming)
        {
            frameMovement.X += _waterFlow.X * FlowPushSpeed * deltaTime;
            frameMovement.Z += _waterFlow.Z * FlowPushSpeed * deltaTime;
        }

        if (IsFlying)
        {
            var verticalInput = 0f;
            if (keyboard.IsKeyPressed(Key.Space)) verticalInput += 1f;
            if (keyboard.IsKeyPressed(Key.ControlLeft)) verticalInput -= 1f;
            frameMovement.Y = verticalInput * moveSpeed * deltaTime;
        }
        else if (swimming)
        {
            // Standing on the bottom of a shallow puddle still jumps, so wading out of one works
            if (IsGrounded && keyboard.IsKeyPressed(Key.Space))
            {
                _verticalVelocity = JumpSpeed;
                IsGrounded = false;
            }
            else
            {
                _verticalVelocity = MathUtils.Damp(_verticalVelocity,
                    keyboard.IsKeyPressed(Key.Space) ? SwimUpSpeed : SinkSpeed, WaterDrag, deltaTime);
            }

            frameMovement.Y = _verticalVelocity * deltaTime;
        }
        else
        {
            if (IsGrounded && keyboard.IsKeyPressed(Key.Space))
            {
                _verticalVelocity = JumpSpeed;
                IsGrounded = false;
            }

            _verticalVelocity -= Gravity * deltaTime;
            frameMovement.Y = _verticalVelocity * deltaTime;
        }

        // Flight is noclip: it moves straight through walls, so no axis stepping.
        if (IsFlying)
        {
            Position += frameMovement;
            IsGrounded = false;
            UpdateShoulderOffset(chunkManager, deltaTime);
            RefreshEyeWaterState(chunkManager);
            return;
        }

        var horizontalStart = Position;
        Position = MoveOnAxis(Position, frameMovement.X, 0, chunkManager, out var collidedX);
        Position = MoveOnAxis(Position, frameMovement.Z, 2, chunkManager, out var collidedZ);

        // Auto-step: on foot and grounded, a low ledge is climbed instead of stopping the
        // move — sprinting clears a full block, walking only half. The raised attempt replays
        // the frame's horizontal move from the pre-move spot, so the climb never overshoots.
        var steppedThisFrame = false;
        if (!swimming && IsGrounded && (collidedX || collidedZ)
            && TryStepUp(horizontalStart, frameMovement,
                IsSprinting ? SprintStepHeight : WalkStepHeight, chunkManager) is { } stepped)
        {
            Position = stepped;
            steppedThisFrame = true;
        }
        if (!steppedThisFrame)
        {
            if (collidedX) _horizontalVelocity.X = 0f;
            if (collidedZ) _horizontalVelocity.Y = 0f;
        }

        // Swimming into a wall while holding Space climbs it. The boost is applied before the
        // vertical move so the lift lands in the same frame as the collision.
        if (swimming && (collidedX || collidedZ) && keyboard.IsKeyPressed(Key.Space))
        {
            _verticalVelocity = MathF.Max(_verticalVelocity, SwimExitBoost);
            frameMovement.Y = _verticalVelocity * deltaTime;
        }

        Position = MoveOnAxis(Position, frameMovement.Y, 1, chunkManager, out var collidedY);

        if (collidedY)
        {
            if (frameMovement.Y < 0f)
                IsGrounded = true;

            _verticalVelocity = 0f;
        }
        else
        {
            IsGrounded = false;
        }

        UpdateShoulderOffset(chunkManager, deltaTime);
        RefreshEyeWaterState(chunkManager);
    }

    /// <summary>
    /// Recomputes <see cref="IsInWater"/> and <see cref="IsEyeInWater"/> from the fluid cells the
    /// body overlaps, and averages those cells' flow directions into the push applied while
    /// swimming. A fluid surface sits below its cell top (8/9 of a block for a source, less
    /// for a flowing skirt), so both tests compare real surface heights instead of merely asking
    /// whether the cell holds water.
    /// </summary>
    private void UpdateWaterState(ChunkManager chunkManager)
    {
        var feetY = Position.Y - EyeHeight;

        var swimMin = new Vector3(Position.X - PlayerRadius, feetY, Position.Z - PlayerRadius);
        var swimMax = new Vector3(Position.X + PlayerRadius, feetY + SwimDepth, Position.Z + PlayerRadius);
        VoxelCoords.CellRange(swimMin, swimMax, out var minX, out var minY, out var minZ, out var maxX, out var maxY, out var maxZ);

        // One pass over the footprint answers both questions: whether any cell is deep enough
        // to swim in, and which way the ones that are actually flow
        var inWater = false;
        var flowSum = Vector3.Zero;
        var flowCells = 0;
        for (var x = minX; x <= maxX; x++)
        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
        {
            if (chunkManager.GetFluidSurfaceGlobal(x, y, z) <= feetY) continue;

            inWater = true;

            var cellFlow = chunkManager.GetFluidFlowGlobal(x, y, z);
            if (cellFlow == Vector3.Zero) continue; // a still cell must not dilute the average

            flowSum += cellFlow;
            flowCells++;
        }

        _waterFlow = flowCells > 0 ? flowSum / flowCells : Vector3.Zero;
        IsInWater = inWater;
        var (eyeX, eyeY, eyeZ) = Position.BlockCell();
        IsEyeInWater = Position.Y < chunkManager.GetFluidSurfaceGlobal(eyeX, eyeY, eyeZ);
        IsEyeInLava = false;
        if (IsEyeInWater && chunkManager.TryGetCell(eyeX, eyeY, eyeZ, out var eyeId, out _)
            && BlockType.IsFluid(eyeId))
        {
            IsEyeInLava = BlockType.Emission[eyeId] > 0;
        }

        UpdateEyeWaterSurface(chunkManager, eyeX, eyeY, eyeZ);

        // Lava burns on body contact, not just eye contact: a wade through a shallow pool
        // keeps the eye dry but still has to hurt.
        var lava = false;
        var lavaMin = new Vector3(Position.X - PlayerRadius, feetY, Position.Z - PlayerRadius);
        var lavaMax = new Vector3(Position.X + PlayerRadius, feetY + PlayerHeight, Position.Z + PlayerRadius);
        VoxelCoords.CellRange(lavaMin, lavaMax, out var bodyMinX, out var bodyMinY, out var bodyMinZ, out var bodyMaxX, out var bodyMaxY, out var bodyMaxZ);
        for (var x = bodyMinX; x <= bodyMaxX && !lava; x++)
        for (var y = bodyMinY; y <= bodyMaxY && !lava; y++)
        for (var z = bodyMinZ; z <= bodyMaxZ && !lava; z++)
        {
            var id = chunkManager.GetBlockGlobal(x, y, z);
            if (BlockType.IsFluid(id) && BlockType.Emission[id] > 0)
                lava = true;
        }
        IsInLava = lava;
    }

    private void UpdateHeadMotion(float deltaTime, float intendedSpeed, bool moving, bool swimming)
    {
        var fovTarget = DefaultFov + SprintFovBoost *
            MathUtils.Clamp01((intendedSpeed - BaseSpeed) / (BaseSpeed * (SprintMultiplier - 1f)));
        _currentFov = MathUtils.Damp(_currentFov, fovTarget, FovDampRate, deltaTime);

        _runBlend = MathUtils.Damp(_runBlend, IsSprinting ? 1f : 0f, RunBlendRate, deltaTime);
        var bobTarget = moving && !IsFlying && (IsGrounded || swimming) ? 1f : 0f;
        _bobBlend = MathUtils.Damp(_bobBlend, bobTarget, BobDampRate, deltaTime);
        if (moving && !IsFlying)
            _bobPhase += deltaTime * (4f + intendedSpeed * 0.9f + _runBlend * 2.5f);

        var height = MathUtils.Lerp(WalkBobHeight, RunBobHeight, _runBlend);
        var strafe = MathUtils.Lerp(WalkBobStrafe, RunBobStrafe, _runBlend);
        _bobHeight = MathF.Sin(_bobPhase * 2f) * height * _bobBlend;
        _bobStrafe = MathF.Cos(_bobPhase) * strafe * _bobBlend;
        _roll = MathF.Sin(_bobPhase) * RunRoll * _runBlend * _bobBlend;
    }

    private void UpdateCreativeToggle(IKeyboard keyboard)
    {
        var creativePressed = keyboard.IsKeyPressed(Key.C);
        if (creativePressed && !_creativeWasPressed)
        {
            IsCreative = !IsCreative;
            if (!IsCreative)
                IsFlying = false;
            _verticalVelocity = 0f;
            Log.WriteLine($"Creative {(IsCreative ? "ON" : "OFF")}");
        }

        _creativeWasPressed = creativePressed;
    }

    private void UpdateFlightToggle(float deltaTime, IKeyboard keyboard)
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
                    _verticalVelocity = 0f;
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

    private void UpdateShoulderToggle(IKeyboard keyboard)
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

    private void UpdateShoulderOffset(ChunkManager chunkManager, float deltaTime)
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
    private void UpdateEyeWaterSurface(ChunkManager chunkManager, int eyeX, int eyeY, int eyeZ)
    {
        if (!IsEyeInWater || IsEyeInLava)
            return;

        var surface = chunkManager.GetFluidColumnTopGlobal(eyeX, eyeY, eyeZ);
        if (float.IsFinite(surface))
            EyeWaterSurfaceY = surface;
    }

    private void RefreshEyeWaterState(ChunkManager chunkManager)
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
    private Vector3? TryStepUp(Vector3 start, Vector3 frameMovement, float stepHeight,
        ChunkManager chunkManager)
    {
        if (stepHeight <= 0f)
            return null;

        var steps = Math.Max(1, (int)MathF.Ceiling(stepHeight / CollisionStepSize));
        for (var i = 1; i <= steps; i++)
        {
            var lift = MathF.Min(i * CollisionStepSize, stepHeight) + StepClearance;
            var lifted = OffsetAxis(start, 1, lift);
            if (IsColliding(lifted, chunkManager))
                continue;

            var stepped = MoveOnAxis(lifted, frameMovement.X, 0, chunkManager, out var blockedX);
            stepped = MoveOnAxis(stepped, frameMovement.Z, 2, chunkManager, out var blockedZ);
            if (blockedX || blockedZ)
                continue;

            // A lift that clears nothing must not teleport the body straight up; only accept
            // it when the horizontal move actually advanced past the start.
            if (MathF.Abs(stepped.X - start.X) < 1e-4f && MathF.Abs(stepped.Z - start.Z) < 1e-4f)
                continue;

            return stepped;
        }

        return null;
    }

    private Vector3 MoveOnAxis(Vector3 start, float delta, int axis, ChunkManager chunkManager, out bool collided)
    {
        collided = false;
        if (MathF.Abs(delta) < float.Epsilon)
            return start;

        var position = start;
        var remaining = delta;

        while (MathF.Abs(remaining) > float.Epsilon)
        {
            var step = MathUtils.ClampStep(remaining, CollisionStepSize);
            var candidate = OffsetAxis(position, axis, step);

            if (IsColliding(candidate, chunkManager))
            {
                collided = true;
                break;
            }

            position = candidate;
            remaining -= step;
        }

        return position;
    }

    private static Vector3 OffsetAxis(Vector3 value, int axis, float delta)
    {
        return axis switch
        {
            0 => new Vector3(value.X + delta, value.Y, value.Z),
            1 => new Vector3(value.X, value.Y + delta, value.Z),
            2 => new Vector3(value.X, value.Y, value.Z + delta),
            _ => value
        };
    }

    private bool IsColliding(Vector3 eyePosition, ChunkManager chunkManager)
    {
        var min = new Vector3(
            eyePosition.X - PlayerRadius,
            eyePosition.Y - EyeHeight,
            eyePosition.Z - PlayerRadius);
        var max = new Vector3(
            eyePosition.X + PlayerRadius,
            eyePosition.Y - EyeHeight + PlayerHeight,
            eyePosition.Z + PlayerRadius);

        VoxelCoords.CellRange(min, max, out var minX, out var minY, out var minZ, out var maxX, out var maxY, out var maxZ);

        for (var x = minX; x <= maxX; x++)
        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
        {
            var blockId = chunkManager.GetBlockGlobal(x, y, z);
            // A layered-snow cell only collides once its stack fills the cell; every other
            // block answers as before. See BlockType.IsSolidCell.
            if (!BlockType.IsSolidCell(blockId, chunkManager.GetStateGlobal(x, y, z))) continue;
            // A pass-through block (leaves) never blocks the body; it only scales the walk
            // while the body overlaps it (see BlockMoveFactor).
            if (BlockType.MovementSpeedOf(blockId) > 0f) continue;
            // Tree wood collides with its drawn node-and-tubes shape and a collidesByHitbox
            // block (a mold slab) with its declared hitboxes, so the body slips past thin limbs,
            // under high branches and onto a low slab; every other solid block keeps the
            // full-cube test it always had.
            if (!BlockType.IsTreeWood(blockId) && !BlockType.CollidesByHitboxOf(blockId)) return true;
            if (TreeWoodShape.OverlapsShape(blockId, chunkManager, x, y, z, min, max))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Movement speed multiplier from the pass-through blocks the body currently occupies: the
    /// smallest <see cref="BlockType.MovementSpeed"/> among them, or 1 when none. A block with a
    /// positive movement speed does not collide (see <see cref="IsColliding"/>) but scales the
    /// player's speed while they wade through it, so leaves slow a walk without stopping it.
    /// </summary>
    private float BlockMoveFactor(ChunkManager chunkManager)
    {
        var min = new Vector3(Position.X - PlayerRadius, Position.Y - EyeHeight, Position.Z - PlayerRadius);
        var max = new Vector3(Position.X + PlayerRadius, Position.Y - EyeHeight + PlayerHeight, Position.Z + PlayerRadius);
        VoxelCoords.CellRange(min, max, out var minX, out var minY, out var minZ, out var maxX, out var maxY, out var maxZ);

        var factor = 1f;
        for (var x = minX; x <= maxX; x++)
        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
        {
            var speed = BlockType.MovementSpeedOf(chunkManager.GetBlockGlobal(x, y, z));
            if (speed > 0f && speed < factor)
                factor = speed;
        }

        return factor;
    }

    internal static float GroundAccelRate(float friction)
    {
        var clamped = Math.Clamp(friction, 0f, 1f);
        var tau = GroundMinResponse + (1f - clamped) * GroundSlideScale;
        tau = Math.Clamp(tau, GroundMinResponse, GroundSlideScale);
        return Math.Clamp(1f / tau, 0.1f, 50f);
    }

    private float SampleGroundFriction(ChunkManager chunkManager)
    {
        var feetY = Position.Y - EyeHeight;
        var groundY = MathUtils.FloorToInt(feetY - 0.02f);
        var minX = MathUtils.FloorToInt(Position.X - PlayerRadius);
        var maxX = MathUtils.FloorToInt(Position.X + PlayerRadius);
        var minZ = MathUtils.FloorToInt(Position.Z - PlayerRadius);
        var maxZ = MathUtils.FloorToInt(Position.Z + PlayerRadius);

        var friction = BlockType.DefaultFriction;
        var found = false;
        for (var x = minX; x <= maxX; x++)
        for (var z = minZ; z <= maxZ; z++)
        {
            var id = chunkManager.GetBlockGlobal(x, groundY, z);
            if (!BlockType.IsSolidCell(id, chunkManager.GetStateGlobal(x, groundY, z))) continue;
            var candidate = BlockType.FrictionOf(id);
            if (!found || candidate < friction)
                friction = candidate;
            found = true;
        }

        return found ? friction : BlockType.DefaultFriction;
    }

    /// <summary>
    /// Whether a block cell at <paramref name="blockPos"/> overlaps the player's body. The cell
    /// spans one full cube from its floored corner; touching a face exactly does not count, so
    /// placing a block under the feet or beside the body stays legal.
    /// </summary>
    public bool OverlapsPlayer(Vector3 blockPos)
    {
        var min = new Vector3(
            Position.X - PlayerRadius,
            Position.Y - EyeHeight,
            Position.Z - PlayerRadius);
        var max = new Vector3(
            Position.X + PlayerRadius,
            Position.Y - EyeHeight + PlayerHeight,
            Position.Z + PlayerRadius);
        var cellMin = blockPos.Floor();
        var cellMax = cellMin + Vector3.One;

        return cellMin.X < max.X && cellMax.X > min.X
            && cellMin.Y < max.Y && cellMax.Y > min.Y
            && cellMin.Z < max.Z && cellMax.Z > min.Z;
    }

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
            Position.X - PlayerRadius,
            Position.Y - EyeHeight,
            Position.Z - PlayerRadius);
        var max = new Vector3(
            Position.X + PlayerRadius,
            Position.Y - EyeHeight + PlayerHeight,
            Position.Z + PlayerRadius);
        var eMin = center - new Vector3(halfExtent);
        var eMax = center + new Vector3(halfExtent);

        var ox = Math.Min(max.X, eMax.X) - Math.Max(min.X, eMin.X);
        var oy = Math.Min(max.Y, eMax.Y) - Math.Max(min.Y, eMin.Y);
        var oz = Math.Min(max.Z, eMax.Z) - Math.Max(min.Z, eMin.Z);
        if (ox <= 0f || oy <= 0f || oz <= 0f) return;

        // Signed shove per axis that moves the body fully clear of the cube; ties go negative.
        // Shallowest first, so the shove moves the player the least.
        var bodyCenterY = Position.Y - EyeHeight + PlayerHeight * 0.5f;
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

            if (!IsColliding(candidate, chunkManager))
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
    public bool BodyTouches(Vector3 center, float halfExtent)
    {
        var min = new Vector3(
            Position.X - PlayerRadius,
            Position.Y - EyeHeight,
            Position.Z - PlayerRadius);
        var max = new Vector3(
            Position.X + PlayerRadius,
            Position.Y - EyeHeight + PlayerHeight,
            Position.Z + PlayerRadius);
        var eMin = center - new Vector3(halfExtent);
        var eMax = center + new Vector3(halfExtent);

        return min.X < eMax.X && max.X > eMin.X
            && min.Y < eMax.Y && max.Y > eMin.Y
            && min.Z < eMax.Z && max.Z > eMin.Z;
    }

    public Matrix4x4 GetViewMatrix()
    {
        var viewDir = ViewDirection;
        var right = Vector3.Normalize(Vector3.Cross(viewDir, Vector3.UnitY));
        var up = Vector3.Transform(Vector3.UnitY,
            Matrix4x4.CreateFromAxisAngle(viewDir, _roll + _shakeRoll));
        var eye = Position + Vector3.UnitY * _bobHeight + right * _bobStrafe + _shoulderOffset + _shakeOffset;
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
            _shakeOffset, _shakeRoll, _currentFov, _aspectRatio);

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

    private Vector3 GetForwardVector() => Vector3Utils.FromYawPitch(Yaw, Pitch);

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
            return BlockRaycaster.Raycast(Position, GetForwardVector(), maxDistance, 0f, stopAtRange: false,
                chunkManager, out hitPosition, out hitNormal, out hitBoxIndex, out _, includeFluids);
        }

        // Crosshair target from the shoulder camera. Its walk is longer than head reach
        // because it starts behind the head; the head check below cuts it back down.
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
        if (!BlockRaycaster.Raycast(Position, toSurface / headDist, maxDistance, 0f, stopAtRange: false,
                chunkManager, out var headHit, out _, out _, out _, includeFluids))
            return false;
        if (headHit != camHit)
            return false;

        (hitPosition, hitNormal, hitBoxIndex) = (camHit, camNormal, camBox);
        return true;
    }

}
