using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Items;
using VoxelEngine.Juice;
using VoxelEngine.Particles;

namespace VoxelEngine.Entities;

/// <summary>
    /// A collectable left behind by mining: usually a block, sometimes a pure item (see
    /// <see cref="Item"/>). A drop pops out where the block was, falls under
/// gravity and rests on the terrain, spinning and breathing in scale, until the player comes
/// close enough — then it accelerates toward them and is collected on contact. Once latched
/// onto the player it flies straight and ignores terrain.
/// </summary>
public class ItemDrop
{
    /// <summary>Edge length of the floating cube, in blocks.</summary>
    public const float Size = 0.35f / 1.5f;

    /// <summary>Item sprites render larger than block cubes so they read at a distance.</summary>
    private const float ItemSizeMultiplier = 1.5f;

    /// <summary>Half the edge length; the drop collides as a cube this far from its center.</summary>
    internal const float HalfExtent = Size * 0.5f;

    private const float SpinRadiansPerSecond = 1.5f;

    // Gentle breathing, as a fraction of Size, over PulsePeriod seconds
    private const float PulseAmount = 0.08f;
    private const float PulsePeriod = 1.4f;

    // Speeds in blocks/s. The drop eases from a drift into a fast whip, so
    // being pulled in reads as deliberate rather than instant.
    private const float DriftSpeed = 2f;
    private const float SuckSpeed = 18f;
    private const float RampSeconds = 0.175f;

    // Close enough to count as collected, so a drop never hovers just short of the target
    private const float CollectDistance = 0.2f;

    // Idle physics (before the drop latches onto the player): gravity, ground friction and a
    // landing bounce so drops hop once or twice instead of stopping dead
    // A landing at or above BounceMinImpact hops back up at BounceDamping of the impact; each hop
    // is smaller, so a drop dies off under the minimum after one or two and settles
    private const float BounceDamping = 0.5f;
    private const float BounceMinImpact = 1f;

    // How hard a flow accelerates a floating drop, in blocks/s^2; against BodyMotion.WaterFriction the
    // drift settles at roughly FlowPushAccel / BodyMotion.WaterFriction blocks/s
    private const float FlowPushAccel = 4f;

    // A drop chasing a player faster than SuckSpeed is collected anyway rather than trailing
    // them forever
    private const float MaxAttractSeconds = 1.5f;

    // Grace period before a fresh drop can be pulled in. Without it a block mined at arm's
    // length is collected the same instant it appears and its spin is never seen.
    private const float PickupDelaySeconds = 0.4f;

    /// <summary>Block or item id this drop collects as (see <see cref="ItemRegistry.IsItem"/>).</summary>
    public ushort BlockId { get; }

    /// <summary>Owned instance payload; never simulated while dropped.</summary>
    public UI.InventoryPanel.ItemStack? Instance { get; internal set; }
    public Vector3 Position { get; private set; }

    private Vector3 _velocity;
    private float _age;
    private readonly float _pickupDelay;
    private float _attractElapsed;
    private bool _attracted;

    /// <summary>What a stuck drop is stuck in (<see cref="DropStick.None"/> for a loose one).</summary>
    public DropStick Stick { get; private set; }

    /// <summary>Whether the drop is a thrown item stuck where it landed: no spin, no physics, tip along <see cref="StuckHeading"/>.</summary>
    public bool IsStuck => Stick != DropStick.None;

    /// <summary>The unit direction a stuck drop's tip points (the flight's heading when it hit).</summary>
    public Vector3 StuckHeading { get; private set; }

    /// <summary>The mob a <see cref="DropStick.Body"/> drop rides; null for a client's mirror of one, which only stays put.</summary>
    public Mob? Host { get; private set; }

    // A hosted drop's center and heading in its host's frame (about the body center, before the facing yaw)
    private Vector3 _hostOffset;
    private Vector3 _hostHeading;

    /// <summary>Sticks the drop in the terrain at its position, tip along the unit <paramref name="heading"/>: it stays
    /// until a collector comes in range (it then flies in like any drop) or the block it hit is gone.</summary>
    internal void StickInTerrain(Vector3 heading)
    {
        Stick = DropStick.Terrain;
        StuckHeading = heading;
        Host = null;
        _velocity = Vector3.Zero;
        IsGrounded = false;
    }

    /// <summary>Sticks the drop in <paramref name="host"/>'s body at its position, tip along the unit
    /// <paramref name="heading"/>: it rides the mob, turning with it, and nobody can take it until <see cref="Unstick"/>
    /// (the mob died or left). A null host is a client's mirror of the server's.</summary>
    internal void StickInBody(Vector3 heading, Mob? host)
    {
        Stick = DropStick.Body;
        StuckHeading = heading;
        Host = host;
        _velocity = Vector3.Zero;
        IsGrounded = false;
        if (host == null) return;
        var toHost = Matrix4x4.CreateRotationY(-host.Facing);
        _hostOffset = Vector3.Transform(Position - host.Position, toHost);
        _hostHeading = Vector3.TransformNormal(heading, toHost);
    }

    /// <summary>Lets a stuck drop go: it falls and behaves as a loose drop from here on.</summary>
    internal void Unstick()
    {
        Stick = DropStick.None;
        Host = null;
        _velocity = Vector3.Zero;
    }

    // A stuck drop's frame: rides its host, or checks that the terrain it is in still stands. True while it stays stuck
    // and out of reach; false once it is loose (the caller then runs the ordinary drop frame).
    private bool StayStuck(bool inRange, ChunkManager chunks)
    {
        if (Stick == DropStick.Body)
        {
            if (Host == null) return true;
            var fromHost = Matrix4x4.CreateRotationY(Host.Facing);
            Position = Host.Position + Vector3.Transform(_hostOffset, fromHost);
            StuckHeading = Vector3.TransformNormal(_hostHeading, fromHost);
            return true;
        }

        var impact = ThrownProjectile.StuckImpact(BlockId, Position, StuckHeading);
        var held = chunks == null
                   || BoxSweep.IsColliding(impact, new Vector3(ThrownProjectile.HalfExtent + StuckHoldMargin), chunks);
        if (held && (!inRange || _age < _pickupDelay)) return true;
        Unstick();
        return false;
    }

    // How far past the projectile's box the terrain holding a stuck drop may be (the sweep stops a step short of it)
    private const float StuckHoldMargin = 0.2f;

    /// <summary>Whether the drop has landed and is resting on the terrain.</summary>
    public bool IsGrounded { get; private set; }

    /// <summary>Whether the drop has committed to flying at the player.</summary>
    public bool IsAttracted => _attracted;

    /// <summary>Continuous spin about the Y axis, in radians.</summary>
    public float Spin => _age * SpinRadiansPerSecond;

    /// <summary>Current edge length, including the idle pulse; items render 1.5x.</summary>
    public float Scale => VisualSize * (1f + PulseAmount * MathUtils.Oscillate(_age, PulsePeriod));

    // Collision stays at the shared Size: the item boost is visual only
    private float VisualSize => Size * (ItemRegistry.IsItem(BlockId) ? ItemSizeMultiplier : 1f);

    /// <param name="pickupDelaySeconds">How long the drop waits before it can be collected; null keeps the
    /// short default grace (see <see cref="PickupDelaySeconds"/>).</param>
    public ItemDrop(ushort blockId, Vector3 position, Vector3? toss = null, float? pickupDelaySeconds = null)
    {
        BlockId = blockId;
        Position = position;
        _pickupDelay = pickupDelaySeconds ?? PickupDelaySeconds;

        // Small random pop so the drop visibly ejects instead of appearing dead still, with an
        // upward bias so a surface-mined block lands back on top rather than inside the floor.
        // A throw adds its own velocity on top.
        var angle = Random.Shared.NextSingle() * MathF.Tau;
        var horizontalSpeed = 0.5f + Random.Shared.NextSingle();
        _velocity = new Vector3(
            MathF.Cos(angle) * horizontalSpeed,
            2f + Random.Shared.NextSingle() * 1.5f,
            MathF.Sin(angle) * horizontalSpeed);
        if (toss.HasValue)
            _velocity += toss.Value;
    }

    /// <summary>
    /// The save-restore path: a drop put back exactly as a save recorded it, with its velocity
    /// and age and no random pop (the public constructor keeps the pop). The age carries the
    /// pickup grace period, so a drop saved before it could be collected still waits out the
    /// rest of it.
    /// </summary>
    internal ItemDrop(ushort blockId, Vector3 position, Vector3 velocity, float age)
    {
        BlockId = blockId;
        Position = position;
        _velocity = velocity;
        _age = age;
        _pickupDelay = PickupDelaySeconds;
    }

    /// <summary>Current velocity in blocks per second (what an entity save records).</summary>
    public Vector3 Velocity => _velocity;

    /// <summary>Seconds since the drop appeared; drives the spin, the pulse and the pickup delay.</summary>
    internal float Age => _age;

    /// <summary>The drop's water-splash memory, stepped by the world from its bottom each frame
    /// (<see cref="WaterSplash.Track"/>).</summary>
    internal WaterSplashTracker Splash { get; } = new();

    /// <summary>Slides the drop horizontally by <paramref name="delta"/> through the shared block sweep, so the
    /// swinging door that moves it can never push it into a solid cell.</summary>
    internal void Nudge(Vector3 delta, ChunkManager chunks)
    {
        if (IsStuck) return;
        var half = new Vector3(HalfExtent);
        Position = BoxSweep.MoveOnAxis(Position, delta.X, 0, half, chunks, out _);
        Position = BoxSweep.MoveOnAxis(Position, delta.Z, 2, half, chunks, out _);
    }

    /// <summary>Adds <paramref name="impulse"/> (blocks per second) to an idle drop's velocity: a blast's
    /// shove (<see cref="BlastImpulse"/>). A drop already flying at a player ignores it.</summary>
    internal void Push(Vector3 impulse)
    {
        if (_attracted || Stick == DropStick.Body) return;
        // A blast shakes a javelin out of the ground
        if (IsStuck) Unstick();
        _velocity += impulse;
    }

    /// <summary>
    /// Lets go of the player the drop was flying at, so it falls back to the ground under its idle
    /// physics: a multiplayer server calls it when no player can take the drop any more (the one
    /// it latched onto left, or every pool filled).
    /// </summary>
    internal void Release()
    {
        _attracted = false;
        _attractElapsed = 0f;
    }

    /// <summary>
    /// Advances the drop, returning true once it reaches <paramref name="target"/> and should be
    /// collected. While idle the drop falls under gravity and collides with blocks;
    /// <paramref name="inRange"/> latches attraction on, so a drop already being
    /// pulled keeps coming even if the player backs away.
    /// </summary>
    public bool Update(float deltaTime, Vector3 target, bool inRange, ChunkManager chunks)
    {
        _age += deltaTime;
        if (IsStuck && StayStuck(inRange, chunks)) return false;

        if (!_attracted)
        {
            ApplyPhysics(deltaTime, chunks);
            if (!inRange || _age < _pickupDelay) return false;
            _attracted = true;
        }

        _attractElapsed += deltaTime;
        if (_attractElapsed >= MaxAttractSeconds) return true;

        var toTarget = target - Position;
        var distance = toTarget.Length();
        if (distance <= CollectDistance) return true;

        var speed = MathUtils.Lerp(DriftSpeed, SuckSpeed,
            MathUtils.EaseInCubic(_attractElapsed / RampSeconds));
        var step = speed * deltaTime;

        // Collect rather than overshoot, so a fast drop never orbits the player
        if (step >= distance) return true;

        Position += toTarget / distance * step;
        return false;
    }

    /// <summary>
    /// Falls under gravity and slides along blocks, axis by axis like the player, so drops land
    /// on top of the terrain instead of sinking through it.
    /// </summary>
    private void ApplyPhysics(float deltaTime, ChunkManager chunks)
    {
        if (deltaTime <= 0f || chunks == null) return;

        // The bottom of the cube decides: a drop resting on the sea floor with its top out of
        // the water should not still be pushed up
        var bottomY = Position.Y - HalfExtent;
        var bottomCell = new Vector3(Position.X, bottomY, Position.Z).BlockCell();
        var surfaceY = chunks.GetFluidSurfaceGlobal(bottomCell.X, bottomCell.Y, bottomCell.Z);
        var floating = bottomY < surfaceY;

        if (floating)
        {
            _velocity.Y = BodyMotion.FloatTowardSurface(_velocity.Y, surfaceY, bottomY, deltaTime);

            // Flowing water carries the drop downstream. Applied before the damping below, which
            // then caps the drift instead of letting it build up indefinitely.
            var flow = chunks.GetFluidFlowGlobal(bottomCell.X, bottomCell.Y, bottomCell.Z);
            _velocity.X += flow.X * FlowPushAccel * deltaTime;
            _velocity.Z += flow.Z * FlowPushAccel * deltaTime;

            // Water also kills the sideways pop, so a drop thrown into a pond stops drifting
            BodyMotion.DragInWater(ref _velocity, deltaTime);
        }
        else
        {
            _velocity.Y = BodyMotion.Fall(_velocity.Y, deltaTime);
        }

        var movement = _velocity * deltaTime;
        var half = new Vector3(HalfExtent);

        Position = BoxSweep.MoveOnAxis(Position, movement.X, 0, half, chunks, out var hitX);
        Position = BoxSweep.MoveOnAxis(Position, movement.Z, 2, half, chunks, out var hitZ);
        Position = BoxSweep.MoveOnAxis(Position, movement.Y, 1, half, chunks, out var hitY);

        // Walls just stop that axis; the floor hops a real landing back up and rests a gentle one
        if (hitX) _velocity.X = 0f;
        if (hitZ) _velocity.Z = 0f;

        if (hitY)
        {
            if (_velocity.Y < 0f)
            {
                // Water absorbs the impact: a sinking drop settles on the bottom rather than
                // bouncing back up through the surface
                var impact = -_velocity.Y;
                _velocity.Y = BodyMotion.Bounce(impact, floating, BounceMinImpact, BounceDamping);
                IsGrounded = _velocity.Y == 0f;
            }
            else
            {
                _velocity.Y = 0f;
            }
        }
        else
        {
            IsGrounded = false;
        }

        // Scrub horizontal speed once settled so drops don't slide forever on flat ground
        if (IsGrounded)
        {
            BodyMotion.ScrubOnGround(ref _velocity, deltaTime);
        }
    }
}
