using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Silk.NET.Input;
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
    public Vector3 ViewDirection => _shoulderViewDir.LengthSquared() > 1e-8f
        ? Vector3.Normalize(_shoulderViewDir)
        : GetForwardVector();
    [Obsolete("Renamed to IsCreative.")]
    public bool IsNoclip => IsCreative;
    public float VerticalVelocity => _verticalVelocity;

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
    private float _verticalVelocity;
    private Vector2 _lastMousePosition;
    private bool _firstMouseMovement = true;
    private bool _spaceWasPressed;
    private bool _creativeWasPressed;
    private float _timeSinceSpacePress = float.MaxValue;
    // Averaged flow of the fluid cells the body is in, refreshed by UpdateWaterState
    private Vector3 _waterFlow;

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
        look = new Vector2(yaw, Math.Clamp(pitch, -89f, 89f));
        return true;
    }

    // Headless tests pass nulls: no window to resize with and no mouse to capture.
    // The game always passes both, so its behaviour is unchanged.
    public Camera(IWindow? window, IInputContext? input)
    {
        this.window = window;
        mouse = input?.Mice.FirstOrDefault();

        if (window != null)
            window.FramebufferResize += size => { _aspectRatio = size.X / (float)size.Y; };

        if (mouse == null) return;
        mouse.Cursor.CursorMode = CursorMode.Disabled;
        mouse.MouseMove += OnMouseMove;
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
        Pitch = Math.Clamp(pitch, -89f, 89f);
        // A teleport is not a fall: without this a scenario dropped at altitude keeps the
        // speed it built up before the jump and plummets through the frame it wanted
        _verticalVelocity = 0f;
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
        Pitch = Math.Clamp(pitch, -89f, 89f);
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

    private static float DampAngle(float current, float target, float rate, float deltaTime)
    {
        var delta = target - current;
        delta -= MathF.Round(delta / 360f) * 360f;
        return current + delta * (1f - MathF.Exp(-rate * Math.Max(0f, deltaTime)));
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

        Pitch = Math.Clamp(Pitch, -89f, 89f);
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
                var motionYaw = MathF.Atan2(moveInput.Z, moveInput.X) * 180f / MathF.PI;
                _orbitPlayerYaw = DampAngle(_orbitPlayerYaw, motionYaw, OrbitTurnRate, deltaTime);
            }
            else if (_hasActionTarget)
            {
                _orbitPlayerYaw = DampAngle(_orbitPlayerYaw, _actionYaw, OrbitTurnRate, deltaTime);
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
        var intendedSpeed = moving ? moveSpeed * moveInput.Length() : 0f;
        UpdateHeadMotion(deltaTime, intendedSpeed, moving, swimming);

        var frameMovement = moveInput * moveSpeed * deltaTime;

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

        Position = MoveOnAxis(Position, frameMovement.X, 0, chunkManager, out var collidedX);
        Position = MoveOnAxis(Position, frameMovement.Z, 2, chunkManager, out var collidedZ);

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

        var minX = (int)MathF.Floor(Position.X - PlayerRadius);
        var maxX = (int)MathF.Floor(Position.X + PlayerRadius);
        var minZ = (int)MathF.Floor(Position.Z - PlayerRadius);
        var maxZ = (int)MathF.Floor(Position.Z + PlayerRadius);
        var minY = (int)MathF.Floor(feetY);
        var maxY = (int)MathF.Floor(feetY + SwimDepth);

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
        var eyeX = (int)MathF.Floor(Position.X);
        var eyeY = (int)MathF.Floor(Position.Y);
        var eyeZ = (int)MathF.Floor(Position.Z);
        IsEyeInWater = Position.Y < chunkManager.GetFluidSurfaceGlobal(eyeX, eyeY, eyeZ);
        IsEyeInLava = false;
        if (IsEyeInWater && chunkManager.TryGetCell(eyeX, eyeY, eyeZ, out var eyeId, out _)
            && BlockType.IsFluid(eyeId))
        {
            IsEyeInLava = BlockType.Emission[eyeId] > 0;
        }
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
        _shoulderViewDir = viewDir.LengthSquared() < 1e-8f ? forward : Vector3.Normalize(viewDir);
    }

    private static bool IsCameraBlocked(Vector3 point, ChunkManager chunkManager)
    {
        var id = chunkManager.GetBlockGlobal(
            (int)MathF.Floor(point.X),
            (int)MathF.Floor(point.Y),
            (int)MathF.Floor(point.Z));
        return BlockType.IsSolid(id);
    }

    private void RefreshEyeWaterState(ChunkManager chunkManager)
    {
        if (!IsShoulderSurf)
            return;

        var eye = Position + _shoulderOffset;
        var eyeX = (int)MathF.Floor(eye.X);
        var eyeY = (int)MathF.Floor(eye.Y);
        var eyeZ = (int)MathF.Floor(eye.Z);
        IsEyeInWater = eye.Y < chunkManager.GetFluidSurfaceGlobal(eyeX, eyeY, eyeZ);
        IsEyeInLava = false;
        if (IsEyeInWater && chunkManager.TryGetCell(eyeX, eyeY, eyeZ, out var eyeId, out _)
            && BlockType.IsFluid(eyeId))
        {
            IsEyeInLava = BlockType.Emission[eyeId] > 0;
        }
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
            var step = Math.Clamp(remaining, -CollisionStepSize, CollisionStepSize);
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

        var minX = (int)MathF.Floor(min.X);
        var minY = (int)MathF.Floor(min.Y);
        var minZ = (int)MathF.Floor(min.Z);
        var maxX = (int)MathF.Floor(max.X);
        var maxY = (int)MathF.Floor(max.Y);
        var maxZ = (int)MathF.Floor(max.Z);

        for (var x = minX; x <= maxX; x++)
        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
        {
            var blockId = chunkManager.GetBlockGlobal(x, y, z);
            if (!BlockType.IsSolid(blockId)) continue;
            // Tapered wood collides with its drawn node-and-arms shape instead of the
            // whole cell, so the body slips past thin limbs and under high arms; every
            // other solid block keeps the full-cube test it always had.
            if (!WoodShape.IsTapered(blockId)) return true;
            if (WoodShape.OverlapsShape(blockId, chunkManager.GetBlockGlobal, x, y, z, min, max))
                return true;
        }

        return false;
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
        var cellMin = new Vector3(
            MathF.Floor(blockPos.X),
            MathF.Floor(blockPos.Y),
            MathF.Floor(blockPos.Z));
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

    public Matrix4x4 GetViewMatrix()
    {
        var viewDir = ViewDirection;
        var right = Vector3.Normalize(Vector3.Cross(viewDir, Vector3.UnitY));
        var up = Vector3.Transform(Vector3.UnitY,
            Matrix4x4.CreateFromAxisAngle(viewDir, _roll));
        var eye = Position + Vector3.UnitY * _bobHeight + right * _bobStrafe + _shoulderOffset;
        return Matrix4x4.CreateLookAt(eye, eye + viewDir, up);
    }

    public Matrix4x4 GetProjectionMatrix() =>
        Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI * _currentFov / 180f, _aspectRatio, NearPlane, FarPlane);

    public Matrix4x4 GetViewProjection() => GetViewMatrix() * GetProjectionMatrix();

    /// <summary>View direction, for aiming the shadow camera ahead of the player.</summary>
    public Vector3 Forward => GetForwardVector();

    private Vector3 GetForwardVector()
    {
        var yawRad = MathF.PI * Yaw / 180f;
        var pitchRad = MathF.PI * Pitch / 180f;

        return Vector3.Normalize(new Vector3(
            MathF.Cos(pitchRad) * MathF.Cos(yawRad),
            MathF.Sin(pitchRad),
            MathF.Cos(pitchRad) * MathF.Sin(yawRad)
        ));
    }

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
            return RaycastFrom(Position, GetForwardVector(), maxDistance, 0f, stopAtRange: false,
                chunkManager, out hitPosition, out hitNormal, out hitBoxIndex, out _, includeFluids);
        }

        // Crosshair target from the shoulder camera. Its walk is longer than head reach
        // because it starts behind the head; the head check below cuts it back down.
        var camRange = maxDistance + _shoulderOffset.Length();
        if (!RaycastFrom(RenderEye, ViewDirection, camRange, camRange * 3 + 2, stopAtRange: true,
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
        if (!RaycastFrom(Position, toSurface / headDist, maxDistance, 0f, stopAtRange: false,
                chunkManager, out var headHit, out _, out _, out _, includeFluids))
            return false;
        if (headHit != camHit)
            return false;

        (hitPosition, hitNormal, hitBoxIndex) = (camHit, camNormal, camBox);
        return true;
    }

    private static bool RaycastFrom(Vector3 origin, Vector3 direction, float range, float cellCap, bool stopAtRange,
        ChunkManager chunkManager, out Vector3 hitPosition, out Vector3 hitNormal, out int hitBoxIndex,
        out float hitT, bool includeFluids)
    {
        // A diagonal ray crosses up to 3 cells per block travelled, so a walk counted in
        // iterations starves rays that travel further (shoulder camera) or aim steeply.
        // Capped walks therefore count cells generously and stop on travelled distance
        // instead; the uncapped walk is first person exactly as it always was. tMax holds
        // the distance to the next cell boundary per axis, so its minimum is where the
        // current cell ends.
        var currentPosition = origin.Floor();
        var step = direction.Sign();
        var tDelta = Vector3.Abs(Vector3.One / direction);
        var dist = Vector3.Distance(Vector3.Zero, direction);
        var stepLimit = stopAtRange ? cellCap : range * dist;
        var lastStepNormal = Vector3.Zero;

        var tMax = new Vector3(
            (step.X > 0 ? currentPosition.X + 1 - origin.X : origin.X - currentPosition.X) * tDelta.X,
            (step.Y > 0 ? currentPosition.Y + 1 - origin.Y : origin.Y - currentPosition.Y) * tDelta.Y,
            (step.Z > 0 ? currentPosition.Z + 1 - origin.Z : origin.Z - currentPosition.Z) * tDelta.Z
        );

        hitPosition = Vector3.Zero;
        hitNormal = Vector3.Zero;
        hitBoxIndex = 0;
        hitT = 0f;

        for (var i = 0; i < stepLimit; i++)
        {
            // currentPosition components are floored floats, so the int casts are exact
            var cellX = (int)currentPosition.X;
            var cellY = (int)currentPosition.Y;
            var cellZ = (int)currentPosition.Z;
            var cellId = chunkManager.GetBlockGlobal(cellX, cellY, cellZ);

            // A fluid is hit only for a caller that asked for it, and then only as a source: a
            // flowing skirt is aimed through (Minecraft's SOURCE_ONLY), and the cell the eye is
            // already inside is skipped so swimming never targets the water on the player's face.
            var targetsCell = cellId != 0 && (!BlockType.IsFluid(cellId)
                || (includeFluids && i > 0 && FluidState.IsSource(chunkManager.GetStateGlobal(cellX, cellY, cellZ))));

            if (targetsCell)
            {
                // Tapered wood aims at its drawn shape, not the static full-height column:
                // the node's vertical extent and side arms follow the parent/child
                // neighbours, so the boxes are resolved from the live neighbourhood.
                var boxes = WoodShape.IsTapered(cellId)
                    ? WoodShape.GetBoxesAt(cellId, chunkManager.GetBlockGlobal, cellX, cellY, cellZ)
                    : BlockType.ById[cellId]?.Hitboxes;
                if (boxes == null || boxes.Length == 0)
                {
                    hitPosition = currentPosition;
                    hitNormal = lastStepNormal == Vector3.Zero ? -direction.Sign() : lastStepNormal;
                    hitBoxIndex = 0;
                    if (!RayIntersectsBox(origin, direction, currentPosition, currentPosition + Vector3.One,
                            float.PositiveInfinity, out var cellT, out _))
                        cellT = 0f;
                    hitT = cellT;
                    return true;
                }

                var bestT = float.PositiveInfinity;
                var bestBox = -1;
                var bestNormal = Vector3.Zero;
                for (var b = 0; b < boxes.Length; b++)
                {
                    var boxMin = currentPosition + boxes[b].Min;
                    var boxMax = currentPosition + boxes[b].Max;
                    if (!RayIntersectsBox(origin, direction, boxMin, boxMax, range, out var t, out var normal))
                        continue;
                    if (t < bestT)
                    {
                        bestT = t;
                        bestBox = b;
                        bestNormal = normal;
                    }
                }

                if (bestBox >= 0)
                {
                    hitPosition = currentPosition;
                    hitBoxIndex = bestBox;
                    hitT = bestT;
                    // The eye inside the box has no entry face: fall back to the cell entry.
                    hitNormal = bestNormal == Vector3.Zero
                        ? (lastStepNormal == Vector3.Zero ? -direction.Sign() : lastStepNormal)
                        : bestNormal;
                    return true;
                }
            }

            if (stopAtRange && Math.Min(tMax.X, Math.Min(tMax.Y, tMax.Z)) > range)
                break;

            if (tMax.X < tMax.Y && tMax.X < tMax.Z)
            {
                currentPosition.X += step.X;
                tMax.X += tDelta.X;
                lastStepNormal = new Vector3(-step.X, 0, 0);
            }
            else if (tMax.Y < tMax.Z)
            {
                currentPosition.Y += step.Y;
                tMax.Y += tDelta.Y;
                lastStepNormal = new Vector3(0, -step.Y, 0);
            }
            else
            {
                currentPosition.Z += step.Z;
                tMax.Z += tDelta.Z;
                lastStepNormal = new Vector3(0, 0, -step.Z);
            }
        }

        return false;
    }

    internal static bool RayIntersectsBox(Vector3 origin, Vector3 direction, Vector3 min, Vector3 max,
        float maxDistance, out float tEnter, out Vector3 normal)
    {
        tEnter = 0f;
        normal = Vector3.Zero;
        var tExit = float.PositiveInfinity;
        var hitAxis = -1;

        for (var axis = 0; axis < 3; axis++)
        {
            var o = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
            var d = axis == 0 ? direction.X : axis == 1 ? direction.Y : direction.Z;
            var lo = axis == 0 ? min.X : axis == 1 ? min.Y : min.Z;
            var hi = axis == 0 ? max.X : axis == 1 ? max.Y : max.Z;

            if (MathF.Abs(d) < 1e-8f)
            {
                if (o < lo || o > hi) return false;
                continue;
            }

            var t1 = (lo - o) / d;
            var t2 = (hi - o) / d;
            var tNear = MathF.Min(t1, t2);
            var tFar = MathF.Max(t1, t2);
            if (tNear > tEnter || (tNear == tEnter && hitAxis < 0))
            {
                tEnter = tNear;
                hitAxis = axis;
            }
            tExit = MathF.Min(tExit, tFar);
            if (tEnter > tExit) return false;
        }

        if (tExit < 0f || tEnter > maxDistance) return false;
        if (tEnter < 0f)
        {
            tEnter = 0f;
            normal = Vector3.Zero;
            return true;
        }

        var sign = (hitAxis == 0 ? direction.X : hitAxis == 1 ? direction.Y : direction.Z) > 0f ? -1f : 1f;
        normal = hitAxis switch
        {
            0 => new Vector3(sign, 0f, 0f),
            1 => new Vector3(0f, sign, 0f),
            _ => new Vector3(0f, 0f, sign),
        };
        return true;
    }
}
