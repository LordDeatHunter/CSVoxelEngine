using System;
using System.Linq;
using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Data;
using VoxelEngine.Entities;
using VoxelEngine.Juice;
using VoxelEngine.Simulation.Fluids;
using VoxelEngine.UI;

namespace VoxelEngine.Player;

public class Camera : IMobTarget
{
    private readonly PlayerPhysics _physics = new();
    private readonly ViewProjectionCache _viewCache;
    private readonly ModeToggles _toggles;
    private readonly HeadMotion _head;
    private readonly ShoulderRig _shoulder;
    private readonly IWindow? window;
    private readonly IMouse? mouse;
    public Vector3 Position { get; private set; }
    public float Yaw { get; private set; } = SpawnLook.X;
    public float Pitch { get; private set; } = SpawnLook.Y;
    public bool IsGrounded { get; private set; }
    public bool IsFlying { get; internal set; }

    /// <summary>
    /// Non-block solids the body collides with (the iceberg boxes): walking, auto-step, the swim
    /// exit and landing all treat them as solid. World sets it once; null means blocks only.
    /// </summary>
    public IBodySolids? BodySolids { get; set; }

    /// <summary>True while sprinting on foot with movement input; drives the run cycle and roll (the FOV
    /// widening reads the sprint key itself, so a flight sprint widens too: see <see cref="SprintFov"/>).</summary>
    public bool IsSprinting { get; private set; }

    /// <summary>Whether the sprint key counts as held this frame: the toggle setting's latch, or
    /// the key itself (<see cref="SprintToggle.Step"/>).</summary>
    public bool SprintHeld => _toggles.SprintHeld;

    /// <summary>Advances the sprint latch from this frame's key state.</summary>
    public void UpdateSprintLatch(bool keyHeld) => _toggles.UpdateSprintLatch(keyHeld);
    /// <summary>True while crouched on foot (Left Ctrl): slower, a lowered view and the model's crouch pose.
    /// The body and its collision keep their size, so the server's movement rules are unaffected.</summary>
    public bool IsSneaking { get; private set; }
    /// <summary>Strokes the swimmer has started so far; moves on each one (see <c>PlayerPhysics.SwimStrokeSeconds</c>).</summary>
    public int SwimStrokeCount => _physics.SwimStrokeCount;
    /// <summary>Current vertical FOV in degrees: the base FOV plus the eased sprint widening plus the punch
    /// pinch plus the throw charge zoom.</summary>
    public float CurrentFov => _head.CurrentFov;
    /// <summary>
    /// The 0..1 charge of the throw the player is winding up, or null when none. The world sets it
    /// every frame from the player's arm (<see cref="PlayerModel.ThrowCharge"/>); the next head-motion
    /// update narrows the view toward it and springs back once it goes null.
    /// </summary>
    public float? ThrowCharge { get; set; }
    /// <summary>How strongly the damage direction arc shows, 0..1 (see <see cref="DamageDirection"/>).</summary>
    public float DamageArcStrength => _head.DamageArc.Intensity;
    /// <summary>
    /// Where on screen the damage direction arc sits: a unit direction from the screen center, x to
    /// the right and y up, recomputed from the current view yaw so it keeps pointing at the attacker
    /// while the player turns.
    /// </summary>
    public Vector2 DamageArcScreenDirection => _head.DamageArc.ScreenDirection(Yaw);
    /// <summary>The droplets streaking down the screen after the eye surfaced from water (see
    /// <see cref="SurfacingDroplets"/>): their strength, age and pattern seed.</summary>
    public SurfacingDroplets Droplets => _head.Droplets;
    /// <summary>
    /// Creative mode (toggled with [C]): walking behaves like survival, but block placing
    /// costs nothing, breaking drops nothing, and double-tapping Space toggles flight.
    /// Flight itself is noclip — it passes through walls.
    /// </summary>
    public bool IsCreative { get; internal set; }
    /// <summary>
    /// Over-shoulder third-person view (F5 mode 2). The player body stays where the
    /// physics put it (<see cref="Position"/> is still the head); the view eye moves behind
    /// + to the side (<see cref="RenderEye"/>) and tilts back onto the aim point
    /// (<see cref="ViewDirection"/>), which <see cref="Raycast(float, ChunkManager, out Vector3, out Vector3, bool)"/> also fires along, so the
    /// highlight lands under the crosshair. Movement, collision and swimming keep reading
    /// <see cref="Position"/>.
    /// </summary>
    public bool IsShoulderSurf => _shoulder.IsShoulderSurf;
    /// <summary>Active F5 camera mode.</summary>
    public CameraMode Mode => _shoulder.Mode;
    /// <summary>
    /// Centered orbit view (F5 mode 3): same rig as shoulder-surf but centered, with the
    /// camera orbiting freely while the player body keeps its own facing
    /// (<see cref="PlayerYaw"/>). Moving or interacting turns the player toward the motion
    /// or the aim, fast-lerped rather than snapped.
    /// </summary>
    public bool IsOrbit => _shoulder.IsOrbit;
    /// <summary>
    /// Yaw the player body faces, in degrees. Tracks the camera in first person and
    /// shoulder-surf; in orbit mode it lerps toward the movement direction or, after a
    /// click, toward the camera aim (see <see cref="NotifyPlayerAction"/>).
    /// </summary>
    public float PlayerYaw => IsOrbit ? OrbitPlayerYaw : Yaw;
    /// <summary>View eye: the head in first person, the pulled-in shoulder camera in third.</summary>
    public Vector3 RenderEye => _shoulder.RenderEye;
    /// <summary>
    /// F5 transition state, 0 in first person and 1 in shoulder-surf, eased over
    /// <c>ShoulderTransitionSeconds</c>. The camera offset, the view direction and the
    /// head opacity all ride this, so toggling never snaps.
    /// </summary>
    public float ShoulderBlend => _shoulder.ShoulderBlend;
    /// <summary>
    /// Direction the screen looks along. Equals the head facing in first person; in
    /// shoulder-surf it tilts from the offset camera back onto the crosshair aim point,
    /// which is also the direction <see cref="Raycast(float, ChunkManager, out Vector3, out Vector3, bool)"/> fires.
    /// </summary>
    public Vector3 ViewDirection => _shoulder.ViewDirection;
    /// <summary>
    /// The unit head ray the last <see cref="Raycast(float, ChunkManager, out Vector3, out Vector3, out int, bool)"/>
    /// fired from <see cref="Position"/>: the facing in first person, the head's re-cast toward the
    /// crosshair's surface point in third person. A multiplayer client sends it with an intent, so
    /// the server's first-person ray from the same eye lands on the same cell.
    /// </summary>
    public Vector3 LastAimDirection => _shoulder.LastAimDirection;
    /// <summary>The body's vertical speed in blocks per second (negative falls).</summary>
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
    /// in the feet..feet+<see cref="PlayerPhysics.SwimDepth"/> band has a fluid surface above the feet. A thin
    /// flowing skirt underfoot is waded through, not swum in.
    /// </summary>
    public bool IsInWater { get; private set; }

    /// <summary>
    /// The share of the body's height under the highest fluid surface over the cells it overlaps,
    /// 0..1 (<see cref="WaterImmersion.Share"/>), refreshed with <see cref="IsInWater"/>.
    /// </summary>
    public float ImmersionShare { get; private set; }

    /// <summary>
    /// How deep the body stands in a fluid (<see cref="WaterImmersion.PhaseOf"/>): wading under half,
    /// floating from half up with the head's eye above the surface, swimming once it is under. Read
    /// from the head's eye even in the shoulder view, since it is the body's state, not the view's.
    /// </summary>
    public WaterPhase WaterPhase { get; private set; }

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

    // Near plane sets depth precision at distance (error ~ z^2 / (near * 2^24)); it must be
    // large enough that stacked LOD levels ~4 blocks apart don't z-fight at 2048 blocks.
    // Collision keeps geometry at least PlayerRadius (0.3) away, so 0.2 never clips.
    internal const float NearPlane = 0.2f;
    internal const float FarPlane = 4096f; // past the outermost LOD ring (2048 blocks + terrain height)
    private static float MouseSensitivity => GameSettings.MouseSensitivity;

    /// <summary>Eye above the feet; the player model roots itself here.</summary>
    public const float EyeHeight = 1.62f;

    // F5 transition: the camera glides and the head fades over this long, eased.
    internal const float ShoulderTransitionSeconds = 0.5f;
    // Orbit player turn speed, in exponential-damp units per second: fast but visibly lerped.
    internal const float OrbitTurnRate = 10f;

    // Parsed once for the whole process, and read by the Yaw/Pitch initializers above
    private static readonly Vector2 SpawnLook = SpawnOverrides.SpawnOrientation();

    private float _aspectRatio = 16f / 10f;

    /// <summary>The landing spring's displacement (positive = the view is dipped).</summary>
    public float LandingDip => _head.LandingDip;

    /// <summary>The hit-kick roll in radians.</summary>
    public float KickRoll => _head.KickRoll;

    /// <summary>The eased strafe tilt in radians: positive leans the view right (see <see cref="StrafeTilt"/>).</summary>
    public float StrafeTiltRoll => _head.StrafeTiltRoll;

    /// <summary>0..1 how far the death view has fallen over; set by the world while a death plays.</summary>
    public float DeathProgress
    {
        set => _head.DeathProgress = value;
    }

    /// <summary>
    /// A hit from the world direction <paramref name="awayHorizontal"/> (the way the blow pushes the
    /// player): the view rolls away from the side it came from and dips, springing back.
    /// </summary>
    public void AddHitKick(Vector3 awayHorizontal, float strength = 1f) => _head.AddHitKick(awayHorizontal, strength);

    /// <summary>Pinches the FOV for a beat: a negative <paramref name="degrees"/> is a zoom-in (an impact).</summary>
    public void PunchFov(float degrees) => _head.PunchFov(degrees);

    /// <summary>
    /// While true the punch pinch holds where it is instead of springing back. The world sets it
    /// while the player's swing is held by a hit-stop (<see cref="PlayerModel.IsHitStopped"/>), so the
    /// view freezes with the arm; it lasts no longer than that bounded hold.
    /// </summary>
    public bool HoldPunchFov { get; set; }

    private Vector2 _lastMousePosition;
    private bool _firstMouseMovement = true;

    /// <summary>
    /// Where a multiplayer client's camera stands until the server's spawn is known: a fixed
    /// point over the origin that reads nothing from worldgen (a client has no terrain of its own).
    /// </summary>
    internal static readonly Vector3 PlaceholderSpawn =
        new(0.5f, WorldGenerationConfig.SeaLevel + 1 + EyeHeight, 0.5f);

    /// <summary>
    /// Builds the camera. Headless tests pass nulls: no window to resize with and no mouse to
    /// capture. The game always passes both, so its behavior is unchanged.
    /// </summary>
    /// <param name="window">The window to follow for the aspect ratio, or null headless.</param>
    /// <param name="input">The input context whose mouse turns the view, or null headless.</param>
    /// <param name="startPosition">
    /// The eye to start at; null searches the worldgen spawn (<c>VOXEL_SPAWN</c>, else the surface
    /// at the origin). A multiplayer client always passes one, so it never reads worldgen here.
    /// </param>
    public Camera(IWindow? window, IInputContext? input, Vector3? startPosition = null)
    {
        _viewCache = new ViewProjectionCache(this);
        _toggles = new ModeToggles(this, _physics);
        _head = new HeadMotion(this);
        _shoulder = new ShoulderRig(this);
        Position = startPosition ?? SpawnOverrides.SpawnPosition();
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
        _shoulder.ResetForTeleport(yaw);
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
        if (fov.HasValue) _head.CurrentFov = fov.Value;
        if (roll.HasValue) _head.Roll = roll.Value;
        if (bobHeight.HasValue) _head.BobHeight = bobHeight.Value;
        if (bobStrafe.HasValue) _head.BobStrafe = bobStrafe.Value;
        if (shoulderOffset.HasValue) _shoulder.ShoulderOffset = shoulderOffset.Value;
        if (shoulderViewDir.HasValue) _shoulder.ShoulderViewDir = shoulderViewDir.Value;
        if (shakeOffset.HasValue) _physics.Shake.SetOffset(shakeOffset.Value);
        if (shakeRoll.HasValue) _physics.Shake.SetRoll(shakeRoll.Value);
    }

    /// <summary>
    /// Tells orbit mode the player just interacted (mine/place/use click): the body turns
    /// toward the camera aim over the next frames while the action itself fires at once.
    /// No-op outside orbit mode, where the body already faces the camera.
    /// </summary>
    public void NotifyPlayerAction() => _shoulder.NotifyPlayerAction();

    /// <summary>
    /// Kicks the damage shake: <paramref name="amount"/> adds to trauma (0..1, clamped),
    /// which <see cref="Update"/> decays back to still over <c>ShakeDecaySeconds</c>.
    /// Callers scale it by the hit — World bills fall and lava ticks through here.
    /// </summary>
    public void AddShake(float amount)
    {
        if (amount <= 0f) return;
        _physics.Shake.Add(amount);
    }

    /// <summary>
    /// While set, mouse movement no longer turns the view (the [E] panel owns the cursor).
    /// Position still updates so reopening the game never jumps the look.
    /// </summary>
    public bool SuppressLook { get; set; }

    private void OnMouseMove(IMouse mouse, Vector2 position)
    {
        if (SuppressLook || _firstMouseMovement)
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
    /// The per-frame view juice: FOV (sprint widening, punch pinch and throw zoom), head bob, landing dip,
    /// crouch drop and roll. <paramref name="sprintHeld"/> is the sprint key, on foot or flying;
    /// <see cref="SprintFov"/> decides whether that actually widens the view.
    /// <paramref name="strafe"/> is the sideways movement input along the camera's right (-1..1),
    /// which <see cref="StrafeTilt"/> turns into a lean.
    /// </summary>
    internal void UpdateHeadMotion(float deltaTime, float intendedSpeed, bool moving, bool swimming,
        bool sprintHeld = false, float strafe = 0f) =>
        _head.UpdateHeadMotion(deltaTime, intendedSpeed, moving, swimming, sprintHeld, strafe);

    /// <summary>
    /// Called instead of the toggle updates while a text field owns the keyboard: the edge
    /// latches read as held, so a C, Space or F5 typed into the field and still down when the
    /// field lets go is not taken for a fresh press on the first free frame.
    /// </summary>
    internal void MarkTogglesHeld()
    {
        _toggles.MarkTogglesHeld();
        _shoulder.MarkToggleHeld();
    }

    internal void UpdateCreativeToggle(IKeyboard keyboard) => _toggles.UpdateCreativeToggle(keyboard);

    internal void UpdateFlightToggle(float deltaTime, IKeyboard keyboard) => _toggles.UpdateFlightToggle(deltaTime, keyboard);

    internal void UpdateShoulderToggle(IKeyboard keyboard) => _shoulder.UpdateShoulderToggle(keyboard);

    internal void UpdateShoulderOffset(ChunkManager chunkManager, float deltaTime) =>
        _shoulder.UpdateShoulderOffset(chunkManager, deltaTime);

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
        if (IsShoulderSurf)
            RefreshShoulderEyeWaterState(chunkManager);
        // The frame's final eye state, the one the underwater tint reads, decides the droplets
        _head.Droplets.Observe(IsEyeInWater, IsEyeInLava);
    }

    private void RefreshShoulderEyeWaterState(ChunkManager chunkManager)
    {
        var eye = Position + _shoulder.ShoulderOffset;
        var (eyeX, eyeY, eyeZ) = eye.BlockCell();
        IsEyeInWater = PlayerBodyProbe.EyeInFluid(eye, chunkManager, out var inLava);
        IsEyeInLava = inLava;

        UpdateEyeWaterSurface(chunkManager, eyeX, eyeY, eyeZ);
    }

    /// <summary>Runs the per-frame movement simulation (see <see cref="PlayerPhysics"/>).</summary>
    public void Update(float deltaTime, IKeyboard? keyboard, ChunkManager chunkManager) =>
        _physics.Update(this, deltaTime, keyboard, chunkManager);

    /// <summary>Test seam for the physics state; the game reaches these through PlayerPhysics and BodyCollision.</summary>
    internal void SetPosition(Vector3 position) => Position = position;
    internal void MoveBy(Vector3 delta) => Position += delta;
    internal void SetGrounded(bool grounded) => IsGrounded = grounded;
    internal void SetSprinting(bool sprinting) => IsSprinting = sprinting;
    internal void SetSneaking(bool sneaking) => IsSneaking = sneaking;

    /// <summary>Scenario seam: selects the camera mode directly (the F5 cycle without the key).</summary>
    internal void TestSetMode(CameraMode mode) => _shoulder.TestSetMode(mode);
    internal void SetMovementFactor(float factor) => MovementFactor = factor;
    internal void SetGroundFriction(float friction) => GroundFriction = friction;
    internal void SetInWater(bool inWater) => IsInWater = inWater;
    internal void SetImmersion(float share, WaterPhase phase)
    {
        ImmersionShare = share;
        WaterPhase = phase;
    }
    internal void SetEyeInWater(bool inWater) => IsEyeInWater = inWater;
    internal void SetInLava(bool inLava) => IsInLava = inLava;
    internal void SetEyeInLava(bool inLava) => IsEyeInLava = inLava;
    internal void SetShoulderBlendT(float t) => _shoulder.ShoulderBlendT = t;
    internal float ShoulderBlendT => _shoulder.ShoulderBlendT;

    internal void SetOrbitPlayerYaw(float yaw) => _shoulder.OrbitPlayerYaw = yaw;
    internal float OrbitPlayerYaw => _shoulder.OrbitPlayerYaw;

    internal bool HasActionTarget => _shoulder.HasActionTarget;

    internal float ActionYaw => _shoulder.ActionYaw;

    internal void ClearActionTarget() => _shoulder.ClearActionTarget();

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
    public void PushOutOf(Vector3 center, float halfExtent, ChunkManager chunkManager) =>
        BodyCollision.PushOutOf(this, center, halfExtent, chunkManager);

    /// <inheritdoc />
    Vector3 IMobTarget.Eye => Position;

    /// <inheritdoc />
    public bool IsImmune => IsCreative || IsFlying;

    /// <summary>
    /// A mob hit on the local player: only the knockback is applied here, and the damage direction
    /// arc turns toward the blow. The <paramref name="damage"/> is otherwise ignored, because
    /// <see cref="World"/> applies the mob manager's <c>DamageDealtThisFrame</c> to its own health
    /// pool (and shakes the view) exactly as before mobs had targets.
    /// </summary>
    public void TakeHit(int damage, Vector3 awayHorizontal, float knockbackSpeed, float lift,
        string? attacker = null)
    {
        if (damage > 0)
        {
            AddDamageDirection(awayHorizontal);
            // Remembered so the world's billing of the hit names the killer in a death message
            LastMobAttacker = attacker;
        }
        AddKnockback(awayHorizontal, knockbackSpeed, lift);
    }

    /// <summary>The display name of the last mob whose hit landed on this camera (see
    /// <see cref="World"/>'s billing), or null.</summary>
    public string? LastMobAttacker { get; private set; }

    /// <summary>
    /// A billed blow pushed the player along <paramref name="awayHorizontal"/>: the damage direction
    /// arc points back toward where it came from and fades (see <see cref="DamageDirection"/>). A
    /// zero direction (a fall, lava) shows no arc. Singleplayer feeds it from <see cref="TakeHit"/>;
    /// a multiplayer client from the knockback of a <c>PlayerStatus</c> that also lowered its health.
    /// </summary>
    public void AddDamageDirection(Vector3 awayHorizontal) => _head.DamageArc.Hit(awayHorizontal);

    public Matrix4x4 GetViewMatrix()
    {
        var viewDir = ViewDirection;
        var right = Vector3.Normalize(Vector3.Cross(viewDir, Vector3.UnitY));
        var up = Vector3.Transform(Vector3.UnitY,
            Matrix4x4.CreateFromAxisAngle(viewDir, _head.Roll + _physics.Shake.Roll));
        var eye = Position + Vector3.UnitY * _head.BobHeight + right * _head.BobStrafe + _shoulder.ShoulderOffset + _physics.Shake.Offset;
        return Matrix4x4.CreateLookAt(eye, eye + viewDir, up);
    }

    public Matrix4x4 GetProjectionMatrix() =>
        Matrix4x4.CreatePerspectiveFieldOfView(MathUtils.ToRadians(CurrentFov), _aspectRatio, NearPlane, FarPlane);

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
    public Matrix4x4 GetViewProjection() => _viewCache.GetViewProjection();

    /// <summary>
    /// The view frustum of <see cref="GetViewProjection"/>, built once per distinct camera
    /// pose (PERF_TODO R11). Three passes - RenderWorld, CullWater and RenderLod - used to
    /// derive this independently of the identical unchanged matrix, paying six plane
    /// extractions and normalizations three times a frame. The shadow and mirror frusta are
    /// built from the shadow cascades' <c>ViewProj</c> / <c>_reflViewProj</c> and are deliberately not
    /// routed through here.
    /// </summary>
    public Frustum GetViewFrustum() => _viewCache.GetViewFrustum();

    /// <summary>
    /// Exactly the mutable state the two matrix builders read. <see cref="GetViewMatrix"/>
    /// reads <see cref="ViewDirection"/> (<c>ShoulderRig.ShoulderViewDir</c> when non-degenerate, else
    /// <see cref="GetForwardVector"/> -> <see cref="Yaw"/>/<see cref="Pitch"/>),
    /// <c>HeadMotion.Roll</c>, <c>ViewShake.Roll</c>, <see cref="Position"/>, <c>HeadMotion.BobHeight</c>,
    /// <c>HeadMotion.BobStrafe</c>, <c>ShoulderRig.ShoulderOffset</c> and <c>ViewShake.Offset</c>;
    /// <see cref="GetProjectionMatrix"/> reads <c>HeadMotion.CurrentFov</c>
    /// and <c>_aspectRatio</c>. <c>NearPlane</c>/<c>FarPlane</c> are <c>const</c> and so are
    /// deliberately not in the key.
    /// </summary>
    internal ViewProjKey CurrentViewProjKey() =>
        new(Position, _shoulder.ShoulderViewDir, _shoulder.ShoulderOffset, Yaw, Pitch, _head.Roll, _head.BobHeight, _head.BobStrafe,
            _physics.Shake.Offset, _physics.Shake.Roll, CurrentFov, _aspectRatio);

    /// <summary>
    /// The inputs of <see cref="GetViewProjection"/>'s cache, and nothing else. Adding a
    /// member here means the matrix builders gained an input; removing one means they lost
    /// it. See the invariant on <see cref="GetViewProjection"/>.
    /// </summary>
    internal readonly record struct ViewProjKey(
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
        out int hitBoxIndex, bool includeFluids = false) =>
        _shoulder.Raycast(maxDistance, chunkManager, out hitPosition, out hitNormal, out hitBoxIndex, includeFluids);

}
