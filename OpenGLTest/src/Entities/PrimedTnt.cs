using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Juice;
using VoxelEngine.Networking;
using VoxelEngine.Particles;
using VoxelEngine.Simulation;

namespace VoxelEngine.Entities;

/// <summary>
/// A primed TNT block: a full-size cube that falls under gravity and collides with blocks axis
/// by axis like the player and the item drops, ticking down its fuse until <see cref="TntManager"/>
/// detonates it. While primed it flashes white, faster as the fuse runs out.
/// </summary>
public class PrimedTnt
{
    /// <summary>Seconds from priming to detonation, set by whatever lit it.</summary>
    public float FuseDuration { get; }

    // Slightly inside a full block so a cube spawned where a block just was rests in the freed
    // cell instead of clipping its solid neighbors
    internal const float HalfExtent = 0.49f;

    /// <summary>The cube's water-splash memory, stepped by the world from its bottom each frame
    /// (<see cref="WaterSplash.Track"/>); a client's mirror keeps its own across snapshots.</summary>
    internal WaterSplashTracker Splash { get; } = new();

    public Vector3 Position { get; private set; }

    /// <summary>The block that primed, so the cube renders as whatever declared
    /// <c>onBreak: prime_tnt</c> instead of one hardcoded type.</summary>
    public ushort BlockId { get; }

    private Vector3 _velocity;
    private float _age;

    /// <summary>Seconds left on the fuse.</summary>
    public float FuseRemaining { get; private set; }

    /// <summary>Whether the fuse has run out and this should detonate.</summary>
    public bool IsDue => FuseRemaining <= 0f;

    /// <summary>0 for a normal textured draw, 1 for full white (square-wave strobe).</summary>
    public float WhiteFlash
    {
        get
        {
            // Blink rate ramps from 4 to ~16 Hz as the fuse burns down
            var progress = 1f - MathUtils.Clamp01(FuseRemaining / FuseDuration);
            var rate = MathUtils.Lerp(4f, 16f, progress);
            return ((int)(_age * rate) & 1) == 0 ? 1f : 0f;
        }
    }

    /// <summary>Current edge length; the cube swells slightly as detonation nears.</summary>
    public float Scale
    {
        get
        {
            var progress = 1f - MathUtils.Clamp01(FuseRemaining / FuseDuration);
            return 1f + 0.1f * progress * (WhiteFlash == 1f ? 1f : 0f);
        }
    }

    public PrimedTnt(Vector3 position, ushort blockId, float fuseSeconds)
    {
        Position = position;
        BlockId = blockId;
        FuseDuration = fuseSeconds;
        FuseRemaining = fuseSeconds;

        // Small random pop so the cube visibly ejects instead of appearing dead still, with an
        // upward bias so surface-primed TNT lands back on top rather than inside the floor
        var angle = Random.Shared.NextSingle() * MathF.Tau;
        var horizontalSpeed = 0.5f + Random.Shared.NextSingle();
        _velocity = new Vector3(
            MathF.Cos(angle) * horizontalSpeed,
            2f + Random.Shared.NextSingle() * 1.5f,
            MathF.Sin(angle) * horizontalSpeed);
    }

    // A mirror: no ejection pop, and the age a cube that has burnt this long would have, so the
    // strobe picks up where the server's is
    private PrimedTnt(Vector3 position, ushort blockId, float fuseDuration, float fuseRemaining)
    {
        Position = position;
        BlockId = blockId;
        FuseDuration = fuseDuration;
        FuseRemaining = fuseRemaining;
        _age = fuseDuration - fuseRemaining;
    }

    /// <summary>
    /// A multiplayer client's display-only copy of a server cube (<see cref="NetEntityKind.PrimedTnt"/>):
    /// it never moves by itself and never detonates, since the server alone runs the fuse and the
    /// blast. <see cref="MirrorSetState"/> applies each snapshot and <see cref="AdvanceFuse"/> keeps
    /// the strobe running between them.
    /// </summary>
    internal static PrimedTnt Mirror(Vector3 position, ushort blockId, float fuseDuration, float fuseRemaining) =>
        new(position, blockId, fuseDuration, fuseRemaining);

    /// <summary>Takes a server snapshot's position and fuse; the strobe clock keeps running.</summary>
    internal void MirrorSetState(Vector3 position, float fuseRemaining)
    {
        Position = position;
        FuseRemaining = fuseRemaining;
    }

    /// <summary>Burns a mirror's fuse down by <paramref name="deltaTime"/> with no physics, never
    /// below zero, so the flash keeps ticking between snapshots.</summary>
    internal void AdvanceFuse(float deltaTime)
    {
        _age += deltaTime;
        FuseRemaining = MathF.Max(0f, FuseRemaining - deltaTime);
    }

    /// <summary>Current velocity in blocks per second.</summary>
    public Vector3 Velocity => _velocity;

    /// <summary>Adds <paramref name="impulse"/> (blocks per second) to the velocity: another blast's
    /// shove (<see cref="BlastImpulse"/>), which gravity and the collisions then play out.</summary>
    public void Push(Vector3 impulse) => _velocity += impulse;

    /// <summary>Advances the fuse and the physics. Main thread only.</summary>
    public void Update(float deltaTime, ChunkManager chunks)
    {
        _age += deltaTime;
        FuseRemaining -= deltaTime;
        ApplyPhysics(deltaTime, chunks);
    }

    /// <summary>
    /// Falls under gravity and slides along blocks, axis by axis like the player, so primed TNT
    /// lands on top of the terrain instead of sinking through it.
    /// </summary>
    private void ApplyPhysics(float deltaTime, ChunkManager chunks)
    {
        if (deltaTime <= 0f || chunks == null) return;

        var (fluidX, fluidY, fluidZ) = Position.BlockCell();
        var submerged = Position.Y < chunks.GetFluidSurfaceGlobal(fluidX, fluidY, fluidZ);

        // A primed cube is heavier than an item drop: it does not float, it just sinks slowly
        _velocity.Y = BodyMotion.SinkOrFall(_velocity.Y, submerged, deltaTime);

        var movement = _velocity * deltaTime;
        var half = new Vector3(HalfExtent);

        Position = BoxSweep.MoveOnAxis(Position, movement.X, 0, half, chunks, out var hitX);
        Position = BoxSweep.MoveOnAxis(Position, movement.Z, 2, half, chunks, out var hitZ);
        Position = BoxSweep.MoveOnAxis(Position, movement.Y, 1, half, chunks, out var hitY);

        // Walls just stop that axis; the floor bounces light impacts and rests on hard ones
        if (hitX) _velocity.X = 0f;
        if (hitZ) _velocity.Z = 0f;

        if (hitY)
        {
            if (_velocity.Y < 0f)
            {
                // Underwater the impact is absorbed instead of bouncing
                var impact = -_velocity.Y;
                _velocity.Y = BodyMotion.Bounce(impact, submerged, BodyMotion.HeavyBounceThreshold, BodyMotion.HeavyBounceDamping);
            }
            else
            {
                _velocity.Y = 0f;
            }
        }

        // Scrub horizontal speed once settled so TNT doesn't slide forever on flat ground
        if (hitY && _velocity.Y == 0f)
        {
            BodyMotion.ScrubOnGround(ref _velocity, deltaTime);
        }
    }
}
