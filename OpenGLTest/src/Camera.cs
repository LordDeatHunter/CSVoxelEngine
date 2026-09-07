using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Windowing;

namespace silkgltriangle;

public class Camera
{
    public Vector3 Position { get; private set; } = SpawnPosition();
    public float Yaw { get; private set; } = SpawnLook.X;
    public float Pitch { get; private set; } = SpawnLook.Y;
    public bool IsGrounded { get; private set; }
    public bool IsFlying { get; private set; }
    public bool IsNoclip { get; private set; }
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

    private const float DefaultFov = 70f;
    // Near plane sets depth precision at distance (error ~ z^2 / (near * 2^24)); it must be
    // large enough that stacked LOD levels ~4 blocks apart don't z-fight at 2048 blocks.
    // Collision keeps geometry at least PlayerRadius (0.3) away, so 0.2 never clips.
    private const float NearPlane = 0.2f;
    private const float FarPlane = 4096f; // past the outermost LOD ring (2048 blocks + terrain height)
    private const float MouseSensitivity = 0.2f;

    private const float BaseSpeed = 8f;
    private const float Gravity = 34f;
    private const float JumpSpeed = 11f;
    private const float PlayerHeight = 1.8f;
    private const float EyeHeight = 1.62f;
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

    // Two Space presses within this window toggle flight
    private const float DoubleTapWindow = 0.3f;

    // Set to "x,y,z" to start the eye at an exact spot instead of the surface spawn
    private const string SpawnOverrideVariable = "VOXEL_SPAWN";

    // Set to "yaw,pitch" in degrees to start facing an exact direction
    private const string LookOverrideVariable = "VOXEL_LOOK";

    // Yaw 0 looks down +X, so the default -90 faces -Z, level with the horizon
    private static readonly Vector2 DefaultLook = new(-90f, 0f);

    // Parsed once for the whole process, and read by the Yaw/Pitch initializers above
    private static readonly Vector2 SpawnLook = SpawnOrientation();

    private float _aspectRatio = 16f / 10f;
    private float _verticalVelocity;
    private Vector2 _lastMousePosition;
    private bool _firstMouseMovement = true;
    private bool _spaceWasPressed;
    private bool _noclipWasPressed;
    private float _timeSinceSpacePress = float.MaxValue;
    // Averaged flow of the fluid cells the body is in, refreshed by UpdateWaterState
    private Vector3 _waterFlow;

    private readonly IWindow window;
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

            Console.WriteLine(
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

            Console.WriteLine(
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

    public Camera(IWindow window, IInputContext input)
    {
        this.window = window;
        mouse = input.Mice.FirstOrDefault();

        window.FramebufferResize += size => { _aspectRatio = size.X / (float)size.Y; };

        if (mouse == null) return;
        mouse.Cursor.CursorMode = CursorMode.Disabled;
        mouse.MouseMove += OnMouseMove;
    }

    private void OnMouseMove(IMouse mouse, Vector2 position)
    {
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

        var moveSpeed = keyboard.IsKeyPressed(Key.ShiftLeft) ? BaseSpeed * 10f : BaseSpeed;
        var direction = GetForwardVector();
        var right = Vector3.Normalize(Vector3.Cross(direction, Vector3.UnitY));
        var forward = Vector3.Normalize(new Vector3(direction.X, 0f, direction.Z));

        Vector3 moveInput = Vector3.Zero;
        if (keyboard.IsKeyPressed(Key.W)) moveInput += forward;
        if (keyboard.IsKeyPressed(Key.S)) moveInput -= forward;
        if (keyboard.IsKeyPressed(Key.A)) moveInput -= right;
        if (keyboard.IsKeyPressed(Key.D)) moveInput += right;

        if (moveInput.LengthSquared() > 1f)
            moveInput = Vector3.Normalize(moveInput);

        UpdateFlightToggle(deltaTime, keyboard);
        UpdateNoclipToggle(keyboard);
        UpdateWaterState(chunkManager);

        // Flight and noclip ignore the water entirely, so a submerged player can still fly out
        var swimming = IsInWater && !IsFlying && !IsNoclip;
        if (swimming) moveSpeed *= SwimSpeedFactor;

        var frameMovement = moveInput * moveSpeed * deltaTime;

        // Flowing water carries the player with it. Added before the horizontal moves below, so
        // the push is still stopped by a wall rather than shoving the player through it.
        if (swimming)
        {
            frameMovement.X += _waterFlow.X * FlowPushSpeed * deltaTime;
            frameMovement.Z += _waterFlow.Z * FlowPushSpeed * deltaTime;
        }

        if (IsFlying || IsNoclip)
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

        if (IsNoclip)
        {
            Position += frameMovement;
            IsGrounded = false;
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

        if (IsFlying)
        {
            IsGrounded = false; // blocked vertical movement just stops while flying
        }
        else if (collidedY)
        {
            if (frameMovement.Y < 0f)
                IsGrounded = true;

            _verticalVelocity = 0f;
        }
        else
        {
            IsGrounded = false;
        }
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
        IsEyeInWater = Position.Y < chunkManager.GetFluidSurfaceGlobal(
            (int)MathF.Floor(Position.X), (int)MathF.Floor(Position.Y), (int)MathF.Floor(Position.Z));
    }

    private void UpdateNoclipToggle(IKeyboard keyboard)
    {
        var noclipPressed = keyboard.IsKeyPressed(Key.C);
        if (noclipPressed && !_noclipWasPressed)
        {
            IsNoclip = !IsNoclip;
            _verticalVelocity = 0f;
            Console.WriteLine($"Noclip {(IsNoclip ? "ON" : "OFF")}");
        }

        _noclipWasPressed = noclipPressed;
    }

    private void UpdateFlightToggle(float deltaTime, IKeyboard keyboard)
    {
        _timeSinceSpacePress += deltaTime;

        var spacePressed = keyboard.IsKeyPressed(Key.Space);
        if (spacePressed && !_spaceWasPressed)
        {
            if (_timeSinceSpacePress <= DoubleTapWindow)
            {
                IsFlying = !IsFlying;
                _verticalVelocity = 0f;
                _timeSinceSpacePress = float.MaxValue; // a toggle requires a fresh double tap
                Console.WriteLine($"Flight {(IsFlying ? "ON" : "OFF")}");
            }
            else
            {
                _timeSinceSpacePress = 0f;
            }
        }

        _spaceWasPressed = spacePressed;
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
            if (IsSolidBlock(x, y, z, chunkManager))
                return true;
        }

        return false;
    }

    private static bool IsSolidBlock(int worldX, int worldY, int worldZ, ChunkManager chunkManager)
        => BlockType.IsSolid(chunkManager.GetBlockGlobal(worldX, worldY, worldZ));

    public Matrix4x4 GetViewMatrix() => Matrix4x4.CreateLookAt(Position, Position + GetForwardVector(), Vector3.UnitY);

    public Matrix4x4 GetProjectionMatrix() =>
        Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI * DefaultFov / 180f, _aspectRatio, NearPlane, FarPlane);

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
    /// Walks the view ray cell by cell (DDA) and reports the first cell it can target. Fluids are
    /// transparent to targeting unless <paramref name="includeFluids"/> is set, which only an item
    /// that works on them (the bucket) asks for.
    /// </summary>
    public bool Raycast(float maxDistance, ChunkManager chunkManager, out Vector3 hitPosition, out Vector3 hitNormal,
        bool includeFluids = false)
    {
        var direction = GetForwardVector();
        var currentPosition = Position.Floor();
        var step = direction.Sign();
        var tDelta = Vector3.Abs(Vector3.One / direction);
        var dist = Vector3.Distance(Vector3.Zero, direction);
        var lastStepNormal = Vector3.Zero;

        var tMax = new Vector3(
            (step.X > 0 ? currentPosition.X + 1 - Position.X : Position.X - currentPosition.X) * tDelta.X,
            (step.Y > 0 ? currentPosition.Y + 1 - Position.Y : Position.Y - currentPosition.Y) * tDelta.Y,
            (step.Z > 0 ? currentPosition.Z + 1 - Position.Z : Position.Z - currentPosition.Z) * tDelta.Z
        );

        hitPosition = Vector3.Zero;
        hitNormal = Vector3.Zero;

        for (var i = 0; i < maxDistance * dist; i++)
        {
            // currentPosition components are floored floats, so the int casts are exact
            var cellX = (int)currentPosition.X;
            var cellY = (int)currentPosition.Y;
            var cellZ = (int)currentPosition.Z;
            var cellId = chunkManager.GetBlockGlobal(cellX, cellY, cellZ);

            // A fluid is hit only for a caller that asked for it, and then only as a source: a
            // flowing skirt is aimed through (Minecraft's SOURCE_ONLY), and the cell the eye is
            // already inside is skipped so swimming never targets the water on the player's face.
            var isHit = cellId != 0 && (!BlockType.IsFluid(cellId)
                || (includeFluids && i > 0 && FluidState.IsSource(chunkManager.GetStateGlobal(cellX, cellY, cellZ))));

            if (isHit)
            {
                hitPosition = currentPosition;

                // If we start inside a solid voxel, approximate with opposite view direction.
                hitNormal = lastStepNormal == Vector3.Zero ? -direction.Sign() : lastStepNormal;

                return true;
            }

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
}