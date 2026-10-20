using System;
using System.Numerics;
using Silk.NET.Input;
using VoxelEngine.Chunks;

namespace VoxelEngine.Player;

/// <summary>
/// The player's movement simulation, extracted from <see cref="Camera"/>: walk/fly/swim input
/// to a body position (moved through the voxel world by <see cref="BodyCollision"/>, which also
/// holds the auto-step), knockback, the water state probes and the damage <see cref="ViewShake"/>
/// it ticks. It reads and mutates the <see cref="Camera"/>'s pose and state through the
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
    // How fast a mob knockback shove decays; larger stops the slide sooner.
    private const float KnockbackDamping = 6f;
    private const float GroundMinResponse = 0.02f;
    private const float GroundSlideScale = 10f;
    private const float AirAccelRate = 6f;
    private const float SwimAccelRate = 4.5f;
    private const float FlyAccelRate = 6f;

    // Auto-step heights: sprinting runs up a full block, plain walking only half a block,
    // so a one-block terrain step still has to be jumped (or run at) on foot.
    private const float WalkStepHeight = 0.5f;
    internal const float SprintStepHeight = 1f;

    // Swimming. Water has no gravity term: vertical speed eases toward a target instead, so it
    // stays capped by SwimUpSpeed/SinkSpeed however long the player stays under.
    internal const float SwimSpeedFactor = 0.5f;
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
    // Water depth states (WaterImmersion). A swimmer stays on the swim physics, off the ground, even
    // while a stroke up bobs it out above the float band, so it is caught by the buoyancy on the way
    // back down instead of falling through the surface; landing on ground ends the latch. A down
    // stroke dives: the dive keeps today's slow sink under water until the eye surfaces again, while
    // a body under water that never dived floats back up.
    private bool _swimLatched;
    private bool _diving;
    private WaterPhase _lastPhase;

    /// <summary>Counts strokes started, so the body can start its stroke animation on the same frame.</summary>
    public int SwimStrokeCount { get; private set; }
    internal const float SinkSpeed = -2f;
    private const float WaterDrag = 6f;
    // Pushing into a bank while holding Space lifts the player over it, so getting out of
    // water never needs a ramp
    internal const float SwimExitBoost = 6f;
    // How far above the feet a fluid surface has to reach before it counts as swimmable; the
    // server's PlayerBodyProbe.InWater reads the same depth
    internal const float SwimDepth = 0.9f;
    // How hard a flow carries a swimmer along it, in blocks/s
    private const float FlowPushSpeed = 2f;

    // The damage view shake; Camera feeds it through AddShake and Update ticks it once a frame.
    internal ViewShake Shake { get; } = new();

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
        camera.UpdateSprintLatch(keyboard.IsKeyPressed(Key.ShiftLeft));
        camera.SetShoulderBlendT(Math.Clamp(camera.ShoulderBlendT
            + (camera.IsShoulderSurf ? deltaTime : -deltaTime) / Camera.ShoulderTransitionSeconds, 0f, 1f));
        UpdateWaterState(camera, chunkManager);
        _knockback.X = MathUtils.Damp(_knockback.X, 0f, KnockbackDamping, deltaTime);
        _knockback.Z = MathUtils.Damp(_knockback.Z, 0f, KnockbackDamping, deltaTime);

        var sprinting = camera.SprintHeld;
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
        // Creative mode on foot swims like survival. Floating and swimming stroke; wading (under
        // half the body in, a one-block pool) walks, slowed, and jumps as on land.
        var phase = camera.WaterPhase;
        var swimming = camera.IsInWater && !camera.IsFlying
                       && (phase >= WaterPhase.Float || (_swimLatched && !camera.IsGrounded));
        var wading = camera.IsInWater && !camera.IsFlying && !swimming;
        if (camera.IsFlying || camera.IsGrounded && !swimming)
            _swimLatched = false;
        else if (swimming)
            _swimLatched = true;
        // The dive ends once the eye is back above the surface, or the body out of the swim
        if (!swimming && !_swimLatched || _lastPhase == WaterPhase.Swim && phase != WaterPhase.Swim)
            _diving = false;
        _lastPhase = phase;
        // Ctrl on foot crouches: slower, never sprinting. While flying Ctrl is "down" instead.
        var sneaking = keyboard.IsKeyPressed(Key.ControlLeft) && !camera.IsFlying && !swimming;
        camera.SetSneaking(sneaking);
        camera.SetSprinting(sprinting && moving && !camera.IsFlying && !swimming && camera.IsGrounded && !sneaking);
        if (swimming) moveSpeed *= SwimSpeedFactor;
        if (wading) moveSpeed *= PlayerConfig.WadeSpeedFactor;
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
                if (!strokeUp && strokeDown)
                    _diving = true;
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
        Shake.Update(deltaTime);

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

        // Flowing water carries the player with it, swimming or wading. Added before the horizontal
        // moves below, so the push is still stopped by a wall rather than shoving the player through it.
        if (swimming || wading)
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
                // A vertical stroke sets the speed, which then glides: toward the gentle sink for a
                // diver, and toward the float height for everyone else (floating at the top of deep
                // water, or under it without having dived), so SinkSpeed stops pulling at the surface
                if (_pendingSwimVertical != 0f)
                {
                    _verticalVelocity = _pendingSwimVertical > 0f
                        ? MathF.Max(_verticalVelocity, _pendingSwimVertical)
                        : MathF.Min(_verticalVelocity, _pendingSwimVertical);
                    _pendingSwimVertical = 0f;
                }
                _verticalVelocity = _diving
                    ? MathUtils.Damp(_verticalVelocity, SinkSpeed, SwimGlideDrag, deltaTime)
                    : MathUtils.Damp(_verticalVelocity, WaterImmersion.BuoyancySpeed(camera.ImmersionShare),
                        PlayerConfig.FloatSettleRate, deltaTime);
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
        var afterX = BodyCollision.MoveOnAxis(camera.Position, frameMovement.X, 0, chunkManager, out var collidedX);
        var afterZ = BodyCollision.MoveOnAxis(afterX, frameMovement.Z, 2, chunkManager, out var collidedZ);
        camera.SetPosition(afterZ);

        // Auto-step: on foot and grounded, a low ledge is climbed instead of stopping the
        // move — sprinting clears a full block, walking only half. The raised attempt replays
        // the frame's horizontal move from the pre-move spot, so the climb never overshoots.
        var steppedThisFrame = false;
        if (!swimming && camera.IsGrounded && (collidedX || collidedZ)
            && BodyCollision.TryStepUp(horizontalStart, frameMovement,
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

        var afterY = BodyCollision.MoveOnAxis(camera.Position, frameMovement.Y, 1, chunkManager, out var collidedY);
        camera.SetPosition(afterY);

        if (collidedY)
        {
            if (frameMovement.Y < 0f)
            {
                camera.SetGrounded(true);
                // A bounce block (a slime block) springs the body back up at a share of its
                // impact; the grounded frame is what the fall tracker reads as a cushioned
                // landing, so it never bills damage. Anything else stops dead.
                var impact = -_verticalVelocity;
                _verticalVelocity = impact >= PlayerConfig.SlimeBlockBounceMinSpeed
                                    && BounceBelow(camera, chunkManager)
                    ? impact * PlayerConfig.SlimeBlockBounceRestitution
                    : 0f;
            }
            else
            {
                _verticalVelocity = 0f;
            }
        }
        else
        {
            camera.SetGrounded(false);
        }

        camera.UpdateShoulderOffset(chunkManager, deltaTime);
        camera.RefreshEyeWaterState(chunkManager);
    }

    /// <summary>
    /// Whether the cell under the body's feet is a bounce block: the block the feet just landed
    /// on, read a hair below the feet so a body resting flush on a block top finds that block
    /// rather than its own (air) cell.
    /// </summary>
    public static bool BounceBelow(Camera camera, ChunkManager chunks)
    {
        var feetY = camera.Position.Y - EyeHeight;
        var cellY = MathUtils.FloorToInt(feetY - 0.05f);
        var id = chunks.GetBlockGlobal(MathUtils.FloorToInt(camera.Position.X), cellY,
            MathUtils.FloorToInt(camera.Position.Z));
        return BlockType.BouncesAt(id);
    }

    /// <summary>
    /// Recomputes <see cref="Camera.IsInWater"/> and <see cref="Camera.IsEyeInWater"/> from the
    /// fluid cells the body overlaps, and averages those cells' flow directions into the push
    /// applied while swimming. A fluid surface sits below its cell top (8/9 of a block for a
    /// source, less for a flowing skirt), so both tests compare real surface heights instead of
    /// merely asking whether the cell holds water. The highest surface over the whole body sets
    /// <see cref="Camera.ImmersionShare"/> and <see cref="Camera.WaterPhase"/> (<see cref="WaterImmersion"/>).
    /// </summary>
    public void UpdateWaterState(Camera camera, ChunkManager chunkManager)
    {
        var position = camera.Position;
        var feetY = position.Y - EyeHeight;

        var swimMin = new Vector3(position.X - PlayerRadius, feetY, position.Z - PlayerRadius);
        var swimMax = new Vector3(position.X + PlayerRadius, feetY + SwimDepth, position.Z + PlayerRadius);
        VoxelCoords.CellRange(swimMin, swimMax, out var minX, out var minY, out var minZ, out var maxX, out var maxY, out var maxZ);
        // The whole body's cells, for how much of it is under (the swim band is their lower part)
        var scanTopY = Math.Max(maxY, MathUtils.FloorToInt(feetY + PlayerHeight));

        // One pass over the footprint answers all three questions: whether any cell in the swim
        // band is deep enough to swim in, which way the ones that are actually flow, and how high
        // the highest fluid surface over the body reaches
        var inWater = false;
        var flowSum = Vector3.Zero;
        var flowCells = 0;
        var surfaceTop = float.NegativeInfinity;
        for (var x = minX; x <= maxX; x++)
        for (var y = minY; y <= scanTopY; y++)
        for (var z = minZ; z <= maxZ; z++)
        {
            var surface = chunkManager.GetFluidSurfaceGlobal(x, y, z);
            if (surface > surfaceTop) surfaceTop = surface;
            if (y > maxY || surface <= feetY) continue;

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
        var share = WaterImmersion.Share(feetY, surfaceTop);
        camera.SetImmersion(share, WaterImmersion.PhaseOf(share, camera.IsEyeInWater));

        // Lava burns on body contact, not just eye contact: a wade through a shallow pool
        // keeps the eye dry but still has to hurt. The one rule lives in PlayerBodyProbe, which
        // the server also reads for every connected player.
        camera.SetInLava(PlayerBodyProbe.InLava(position, chunkManager));
    }

    /// <summary>
    /// Movement speed multiplier from the pass-through blocks the body currently occupies: the
    /// smallest <see cref="BlockType.MovementSpeedOf"/> among them, or 1 when none. A block with a
    /// positive movement speed does not collide (see <see cref="BodyCollision.BodyCollides"/>) but scales the
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
