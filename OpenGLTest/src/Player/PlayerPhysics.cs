using System;
using System.Numerics;
using Silk.NET.Input;

namespace silkgltriangle;

/// <summary>
/// The player's movement simulation, extracted from <see cref="Camera"/>: walk/fly/swim input
/// to a body position, collision against the voxel world, auto-step, knockback and the water
/// state probes. It reads and mutates the <see cref="Camera"/>'s pose and state through the
/// public/internal members Camera exposes, so the camera keeps owning the body while the
/// per-frame movement logic lives here.
/// </summary>
internal sealed class PlayerPhysics
{
    public const float BaseSpeed = 8f;
    // Shift while walking is a regular sprint; the giga boost is flight-only.
    public const float SprintMultiplier = 1.5f;
    private const float FlyBoostMultiplier = 10f;
    // Internal, like the swim speeds below, so the server's movement rules are tested against the
    // very motion the client simulates (MovementRulesTests)
    internal const float Gravity = 34f;
    internal const float JumpSpeed = 11f;
    public const float PlayerHeight = 1.8f;
    public const float PlayerRadius = 0.30f;
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
    internal const float SprintStepHeight = 1f;
    private const float StepClearance = 0.001f;

    // Swimming. Water has no gravity term: vertical speed eases toward a target instead, so it
    // stays capped by SwimUpSpeed/SinkSpeed however long the player stays under.
    private const float SwimSpeedFactor = 0.5f;
    // Crouched walking speed as a share of the base walk.
    private const float SneakSpeedFactor = 0.4f;
    internal const float SwimUpSpeed = 4f;

    // Swimming is a series of strokes rather than a steady paddle. A stroke is the arms thrusting out
    // from in front of the body to the sides, and it kicks the swimmer in the direction of the
    // movement input: the speed is *set* to SwimBurstSpeed at that instant and then glides off under
    // SwimGlideDrag until the next stroke is ready, SwimStrokeSeconds after the last (the arms
    // recover slowly to the front in the meantime; PlayerModel runs the same clock). Space and Ctrl
    // stroke straight up and down. The first stroke after entering the water is ready at once.
    internal const float SwimStrokeSeconds = 1.15f;
    internal const float SwimBurstSpeed = 6.5f;
    internal const float SwimBurstUp = 9f;
    internal const float SwimBurstDown = -6f;
    internal const float SwimGlideDrag = 1.6f;
    // Reaching a bank still lifts the swimmer out through SwimExitBoost below, which is the one
    // vertical speed here the server's rise rule is documented against; a stroke stays under it.

    private float _swimStrokeTimer = SwimStrokeSeconds;
    private float _pendingSwimVertical;

    /// <summary>Counts strokes started, so the body can start its stroke animation on the same frame.</summary>
    public int SwimStrokeCount { get; private set; }
    private const float SinkSpeed = -2f;
    private const float WaterDrag = 6f;
    // Pushing into a bank while holding Space lifts the player over it, so getting out of
    // water never needs a ramp
    internal const float SwimExitBoost = 6f;
    // How far above the feet a fluid surface has to reach before it counts as swimmable; the
    // server's PlayerBodyProbe.InWater reads the same depth
    internal const float SwimDepth = 0.9f;
    // How hard a flow carries a swimmer along it, in blocks/s
    private const float FlowPushSpeed = 2f;

    // Damage shake: trauma 0..1, fed by AddShake and decayed in Update. The view offset
    // and roll ride trauma-squared, so small hits tremble and big hits lurch.
    private float _trauma;
    private float _shakeTime;
    public Vector3 ShakeOffset { get; private set; } = Vector3.Zero;
    public float ShakeRoll { get; private set; }
    private const float ShakeDecaySeconds = 0.7f;
    private const float ShakeMaxOffset = 0.3f;
    private const float ShakeMaxRoll = 0.04f;

    private float _verticalVelocity;
    // Averaged flow of the fluid cells the body is in, refreshed by UpdateWaterState
    private Vector3 _waterFlow;
    // Decaying horizontal shove from a mob hit, integrated into the walk each frame
    private Vector3 _knockback;
    private Vector2 _horizontalVelocity;

    /// <summary>Vertical speed in blocks/s (positive up).</summary>
    public float VerticalVelocity => _verticalVelocity;
    /// <summary>Horizontal (x, z) speed in blocks/s.</summary>
    public Vector2 HorizontalVelocity => _horizontalVelocity;

    /// <summary>Test seam: sets the damage-shake view offset directly.</summary>
    public void SetShakeOffset(Vector3 offset) => ShakeOffset = offset;
    /// <summary>Test seam: sets the damage-shake roll directly.</summary>
    public void SetShakeRoll(float roll) => ShakeRoll = roll;

    /// <summary>Feet-relative eye height of the body being simulated.</summary>
    public const float EyeHeight = Camera.EyeHeight;

    /// <summary>Clears the per-frame velocity and trauma state (a teleport is not a fall).</summary>
    public void ResetMotion()
    {
        _verticalVelocity = 0f;
        _knockback = Vector3.Zero;
        _horizontalVelocity = Vector2.Zero;
    }

    /// <summary>
    /// Drops only the vertical speed: a creative or flight toggle must not stop a run or a
    /// knockback mid-stride, it only has to forget the fall it was in.
    /// </summary>
    public void ResetVerticalVelocity() => _verticalVelocity = 0f;

    /// <summary>
    /// Kicks the damage shake: <paramref name="amount"/> adds to trauma (0..1, clamped),
    /// which <see cref="UpdateShake"/> decays back to still over <c>ShakeDecaySeconds</c>.
    /// Callers scale it by the hit — World bills fall and lava ticks through here.
    /// </summary>
    public void AddShake(float amount) => _trauma = Math.Min(1f, _trauma + amount);

    /// <summary>
    /// Knocks the player back from a mob hit: a horizontal shove that decays over a few tenths of
    /// a second — the normal collision still stops it, so a hit can never push the player through
    /// a wall — plus a small upward pop. Flight ignores both (and cannot be hit).
    /// </summary>
    public void AddKnockback(Camera camera, Vector3 horizontalDirection, float horizontalSpeed, float lift)
    {
        horizontalDirection.Y = 0f;
        if (horizontalDirection.LengthSquared() > 1e-6f)
        {
            horizontalDirection = Vector3.Normalize(horizontalDirection);
            _knockback.X = horizontalDirection.X * horizontalSpeed;
            _knockback.Z = horizontalDirection.Z * horizontalSpeed;
        }
        if (!camera.IsFlying && lift > 0f)
            _verticalVelocity = MathF.Max(_verticalVelocity, lift);
    }

    private void UpdateShake(float deltaTime)
    {
        _shakeTime += Math.Max(0f, deltaTime);
        if (_trauma <= 0f)
        {
            ShakeOffset = Vector3.Zero;
            ShakeRoll = 0f;
            return;
        }

        _trauma = Math.Max(0f, _trauma - Math.Max(0f, deltaTime) / ShakeDecaySeconds);
        // Squared falloff plus mismatched sines per axis, so the view shudders instead of
        // swaying on one clean frequency.
        var t = _trauma * _trauma;
        var time = _shakeTime;
        ShakeOffset = new Vector3(
            MathF.Sin(time * 39.7f) * ShakeMaxOffset * t,
            MathF.Sin(time * 44.3f + 1.3f) * ShakeMaxOffset * 0.6f * t,
            MathF.Sin(time * 31.9f + 2.1f) * ShakeMaxOffset * t);
        ShakeRoll = MathF.Sin(time * 47.7f + 0.7f) * ShakeMaxRoll * t;
    }

    /// <summary>
    /// The per-frame movement simulation. Camera.Awake calls this with the input; the body pose
    /// and modes (<see cref="Camera.Position"/>, Yaw, IsFlying, IsCreative) are read and written
    /// here.
    /// </summary>
    public void Update(Camera camera, float deltaTime, IKeyboard? keyboard, ChunkManager chunkManager)
    {
        if (keyboard == null)
            camera.MarkTogglesHeld();
        if (keyboard == null || chunkManager == null)
            return;

        camera.UpdateCreativeToggle(keyboard);
        camera.UpdateFlightToggle(deltaTime, keyboard);
        camera.UpdateShoulderToggle(keyboard);
        camera.SetShoulderBlendT(Math.Clamp(camera.ShoulderBlendT
            + (camera.IsShoulderSurf ? deltaTime : -deltaTime) / Camera.ShoulderTransitionSeconds, 0f, 1f));
        UpdateWaterState(camera, chunkManager);
        _knockback.X = MathUtils.Damp(_knockback.X, 0f, KnockbackDamping, deltaTime);
        _knockback.Z = MathUtils.Damp(_knockback.Z, 0f, KnockbackDamping, deltaTime);

        var sprinting = keyboard.IsKeyPressed(Key.ShiftLeft);
        var moveSpeed = BaseSpeed * (sprinting ? (camera.IsFlying ? FlyBoostMultiplier : SprintMultiplier) : 1f);
        var direction = camera.IsOrbit ? camera.ViewDirection : camera.GetForwardVector();
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
        var backing = !camera.IsOrbit && keyboard.IsKeyPressed(Key.S) && !keyboard.IsKeyPressed(Key.W);
        if (backing && !camera.IsFlying) moveSpeed *= 0.3f;

        // In orbit the body turns toward the motion (or, after a click, toward the aim)
        // while the camera orbits freely; everywhere else it already faces the camera.
        var moving = moveInput.LengthSquared() > 0.01f;
        if (camera.IsOrbit)
        {
            if (moving)
            {
                var motionYaw = MathUtils.ToDegrees(MathF.Atan2(moveInput.Z, moveInput.X));
                camera.SetOrbitPlayerYaw(MathUtils.DampAngle(camera.OrbitPlayerYaw, motionYaw, Camera.OrbitTurnRate, deltaTime));
            }
            else if (camera.HasActionTarget)
            {
                camera.SetOrbitPlayerYaw(MathUtils.DampAngle(camera.OrbitPlayerYaw, camera.ActionYaw, Camera.OrbitTurnRate, deltaTime));
                var remaining = camera.ActionYaw - camera.OrbitPlayerYaw;
                remaining -= MathF.Round(remaining / 360f) * 360f;
                if (Math.Abs(remaining) < 0.5f)
                    camera.ClearActionTarget();
            }
        }
        else
        {
            camera.SetOrbitPlayerYaw(camera.Yaw);
        }

        // Flight ignores the water entirely, so a submerged player can still fly out.
        // Creative mode on foot swims like survival.
        var swimming = camera.IsInWater && !camera.IsFlying;
        // Ctrl on foot crouches: slower, never sprinting. While flying Ctrl is "down" instead.
        var sneaking = keyboard.IsKeyPressed(Key.ControlLeft) && !camera.IsFlying && !swimming;
        camera.SetSneaking(sneaking);
        camera.SetSprinting(sprinting && moving && !camera.IsFlying && !swimming && camera.IsGrounded && !sneaking);
        if (swimming) moveSpeed *= SwimSpeedFactor;
        if (sneaking) moveSpeed = BaseSpeed * SneakSpeedFactor;
        // Pass-through blocks scale the walk instead of blocking it; flight is noclip.
        camera.SetMovementFactor(BlockMoveFactor(camera, chunkManager));
        if (!camera.IsFlying) moveSpeed *= camera.MovementFactor;
        var wish = new Vector2(moveInput.X * moveSpeed, moveInput.Z * moveSpeed);
        float accelRate;
        if (camera.IsFlying)
            accelRate = FlyAccelRate;
        else if (swimming)
        {
            // No steady paddle: the speed only changes at a stroke and glides off in between.
            accelRate = SwimGlideDrag;
            wish = Vector2.Zero;
            _swimStrokeTimer += deltaTime;
            var strokeUp = keyboard.IsKeyPressed(Key.Space);
            var strokeDown = keyboard.IsKeyPressed(Key.ControlLeft);
            if (_swimStrokeTimer >= SwimStrokeSeconds && (moving || strokeUp || strokeDown))
            {
                _swimStrokeTimer = 0f;
                SwimStrokeCount++;
                if (moving)
                {
                    var burst = SwimBurstSpeed * (backing ? 0.6f : 1f) * camera.MovementFactor;
                    _horizontalVelocity = new Vector2(moveInput.X, moveInput.Z) * burst;
                }
                _pendingSwimVertical = strokeUp ? SwimBurstUp : strokeDown ? SwimBurstDown : 0f;
            }
        }
        else if (camera.IsGrounded)
        {
            camera.SetGroundFriction(SampleGroundFriction(camera, chunkManager));
            accelRate = GroundAccelRate(camera.GroundFriction);
        }
        else
        {
            accelRate = AirAccelRate;
        }
        if (!swimming) _swimStrokeTimer = SwimStrokeSeconds;
        _horizontalVelocity.X = MathUtils.Damp(_horizontalVelocity.X, wish.X, accelRate, deltaTime);
        _horizontalVelocity.Y = MathUtils.Damp(_horizontalVelocity.Y, wish.Y, accelRate, deltaTime);

        var intendedSpeed = _horizontalVelocity.Length();
        camera.UpdateHeadMotion(deltaTime, intendedSpeed, moving, swimming, sprintHeld: sprinting,
            strafe: Vector3.Dot(moveInput, right));
        UpdateShake(deltaTime);

        var frameMovement = new Vector3(_horizontalVelocity.X * deltaTime, 0f, _horizontalVelocity.Y * deltaTime);

        // Mob knockback rides the walk like the water flow below: a decaying horizontal shove that
        // the same axis moves still stop against a wall. Flight clears it (it cannot be hit).
        if (camera.IsFlying)
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

        if (camera.IsFlying)
        {
            var verticalInput = 0f;
            if (keyboard.IsKeyPressed(Key.Space)) verticalInput += 1f;
            if (keyboard.IsKeyPressed(Key.ControlLeft)) verticalInput -= 1f;
            frameMovement.Y = verticalInput * moveSpeed * deltaTime;
        }
        else if (swimming)
        {
            // Standing on the bottom of a shallow puddle still jumps, so wading out of one works
            if (camera.IsGrounded && keyboard.IsKeyPressed(Key.Space))
            {
                _verticalVelocity = JumpSpeed;
                camera.SetGrounded(false);
            }
            else
            {
                // A vertical stroke sets the speed, which then glides toward the gentle sink.
                if (_pendingSwimVertical != 0f)
                {
                    _verticalVelocity = _pendingSwimVertical > 0f
                        ? MathF.Max(_verticalVelocity, _pendingSwimVertical)
                        : MathF.Min(_verticalVelocity, _pendingSwimVertical);
                    _pendingSwimVertical = 0f;
                }
                _verticalVelocity = MathUtils.Damp(_verticalVelocity, SinkSpeed, SwimGlideDrag, deltaTime);
            }

            frameMovement.Y = _verticalVelocity * deltaTime;
        }
        else
        {
            if (camera.IsGrounded && keyboard.IsKeyPressed(Key.Space))
            {
                _verticalVelocity = JumpSpeed;
                camera.SetGrounded(false);
            }

            _verticalVelocity -= Gravity * deltaTime;
            frameMovement.Y = _verticalVelocity * deltaTime;
        }

        // Flight is noclip: it moves straight through walls, so no axis stepping.
        if (camera.IsFlying)
        {
            camera.MoveBy(frameMovement);
            camera.SetGrounded(false);
            camera.UpdateShoulderOffset(chunkManager, deltaTime);
            camera.RefreshEyeWaterState(chunkManager);
            return;
        }

        var horizontalStart = camera.Position;
        var afterX = MoveOnAxis(camera.Position, frameMovement.X, 0, chunkManager, out var collidedX);
        var afterZ = MoveOnAxis(afterX, frameMovement.Z, 2, chunkManager, out var collidedZ);
        camera.SetPosition(afterZ);

        // Auto-step: on foot and grounded, a low ledge is climbed instead of stopping the
        // move — sprinting clears a full block, walking only half. The raised attempt replays
        // the frame's horizontal move from the pre-move spot, so the climb never overshoots.
        var steppedThisFrame = false;
        if (!swimming && camera.IsGrounded && (collidedX || collidedZ)
            && TryStepUp(horizontalStart, frameMovement,
                camera.IsSprinting ? SprintStepHeight : WalkStepHeight, chunkManager) is { } stepped)
        {
            camera.SetPosition(stepped);
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

        var afterY = MoveOnAxis(camera.Position, frameMovement.Y, 1, chunkManager, out var collidedY);
        camera.SetPosition(afterY);

        if (collidedY)
        {
            if (frameMovement.Y < 0f)
                camera.SetGrounded(true);

            _verticalVelocity = 0f;
        }
        else
        {
            camera.SetGrounded(false);
        }

        camera.UpdateShoulderOffset(chunkManager, deltaTime);
        camera.RefreshEyeWaterState(chunkManager);
    }

    /// <summary>
    /// Recomputes <see cref="Camera.IsInWater"/> and <see cref="Camera.IsEyeInWater"/> from the
    /// fluid cells the body overlaps, and averages those cells' flow directions into the push
    /// applied while swimming. A fluid surface sits below its cell top (8/9 of a block for a
    /// source, less for a flowing skirt), so both tests compare real surface heights instead of
    /// merely asking whether the cell holds water.
    /// </summary>
    public void UpdateWaterState(Camera camera, ChunkManager chunkManager)
    {
        var position = camera.Position;
        var feetY = position.Y - EyeHeight;

        var swimMin = new Vector3(position.X - PlayerRadius, feetY, position.Z - PlayerRadius);
        var swimMax = new Vector3(position.X + PlayerRadius, feetY + SwimDepth, position.Z + PlayerRadius);
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
        camera.SetInWater(inWater);
        var (eyeX, eyeY, eyeZ) = position.BlockCell();
        camera.SetEyeInWater(position.Y < chunkManager.GetFluidSurfaceGlobal(eyeX, eyeY, eyeZ));
        camera.SetEyeInLava(false);
        if (camera.IsEyeInWater && chunkManager.TryGetCell(eyeX, eyeY, eyeZ, out var eyeId, out _)
            && BlockType.IsFluid(eyeId))
        {
            camera.SetEyeInLava(BlockType.Emission[eyeId] > 0);
        }

        camera.UpdateEyeWaterSurface(chunkManager, eyeX, eyeY, eyeZ);

        // Lava burns on body contact, not just eye contact: a wade through a shallow pool
        // keeps the eye dry but still has to hurt. The one rule lives in PlayerBodyProbe, which
        // the server also reads for every connected player.
        camera.SetInLava(PlayerBodyProbe.InLava(position, chunkManager));
    }

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

    /// <summary>
    /// Whether the body box at <paramref name="eyePosition"/> overlaps a solid or collidesByHitbox
    /// cell. Player body only: drops, slimes and falling blocks still see the full cell.
    /// </summary>
    public bool IsColliding(Vector3 eyePosition, ChunkManager chunkManager) =>
        BodyCollides(eyePosition, chunkManager);

    /// <summary>
    /// The player-body collision rule behind <see cref="IsColliding"/>, with no simulation state,
    /// so the server can judge a connected player's body (<see cref="PlayerBodyProbe"/>) by the
    /// very rule the client's own physics moves it under. An unloaded cell reads as air.
    /// </summary>
    public static bool BodyCollides(Vector3 eyePosition, ChunkManager chunkManager)
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
    /// smallest <see cref="BlockType.MovementSpeedOf"/> among them, or 1 when none. A block with a
    /// positive movement speed does not collide (see <see cref="IsColliding"/>) but scales the
    /// player's speed while they wade through it, so leaves slow a walk without stopping it.
    /// </summary>
    private float BlockMoveFactor(Camera camera, ChunkManager chunkManager)
    {
        var position = camera.Position;
        var min = new Vector3(position.X - PlayerRadius, position.Y - EyeHeight, position.Z - PlayerRadius);
        var max = new Vector3(position.X + PlayerRadius, position.Y - EyeHeight + PlayerHeight, position.Z + PlayerRadius);
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

    private float SampleGroundFriction(Camera camera, ChunkManager chunkManager)
    {
        var position = camera.Position;
        var feetY = position.Y - EyeHeight;
        var groundY = MathUtils.FloorToInt(feetY - 0.02f);
        var minX = MathUtils.FloorToInt(position.X - PlayerRadius);
        var maxX = MathUtils.FloorToInt(position.X + PlayerRadius);
        var minZ = MathUtils.FloorToInt(position.Z - PlayerRadius);
        var maxZ = MathUtils.FloorToInt(position.Z + PlayerRadius);

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
}
