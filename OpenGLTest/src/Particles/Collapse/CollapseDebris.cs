using System;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Particles.Collapse;

/// <summary>
/// One block thrown off a collapsing battle tower (<see cref="TowerCollapseEffects"/>): a cube textured as the block that
/// fell, tumbling through the air under gravity, bouncing off the terrain and rolling along it until its life runs out,
/// when <see cref="TowerCollapseEffects"/> bursts it into rubble chips. Purely visual: it only reads the terrain (the
/// shared <see cref="BoxSweep"/>), never writes a cell, never touches a body, deals no damage and is never saved or sent.
/// Pure CPU state, drawn by <see cref="Rendering.Actors.ObjectPasses.RenderCollapseDebris"/>. Main thread only.
/// </summary>
public sealed class CollapseDebris
{
    private Vector3 _velocity;
    private Vector3 _tumbleAxis;
    private readonly float _tumbleRate;
    private readonly float _lifetime;
    private float _age;

    /// <param name="blockId">The block the cube is drawn as.</param>
    /// <param name="position">The cube's center, in world blocks.</param>
    /// <param name="velocity">Its launch, in blocks per second.</param>
    /// <param name="size">Its edge length, in blocks.</param>
    /// <param name="lifetime">Seconds until it bursts.</param>
    /// <param name="tumbleAxis">The axis it tumbles about in the air (normalized here).</param>
    /// <param name="tumbleRate">How fast it tumbles in the air, in radians per second.</param>
    public CollapseDebris(byte blockId, Vector3 position, Vector3 velocity, float size, float lifetime,
        Vector3 tumbleAxis, float tumbleRate)
    {
        BlockId = blockId;
        Position = position;
        Size = size;
        _velocity = velocity;
        _lifetime = lifetime;
        _tumbleAxis = tumbleAxis.LengthSquared() > 0f ? Vector3.Normalize(tumbleAxis) : Vector3.UnitX;
        _tumbleRate = tumbleRate;
    }

    /// <summary>The block the cube is drawn as.</summary>
    public byte BlockId { get; }

    /// <summary>The cube's center, in world blocks.</summary>
    public Vector3 Position { get; private set; }

    /// <summary>Its edge length, in blocks.</summary>
    public float Size { get; }

    /// <summary>Its rotation about its center.</summary>
    public Quaternion Orientation { get; private set; } = Quaternion.Identity;

    /// <summary>
    /// Advances the cube by <paramref name="deltaTime"/>: it falls (or sinks through water) under the loose-body gravity,
    /// sweeps axis by axis against the solid cells, bounces a hard landing, stops against a wall and rolls on along the
    /// ground, scrubbed by <see cref="JuiceConfig.TowerDebrisRollFriction"/>. Returns false once its life is over.
    /// </summary>
    public bool Update(float deltaTime, ChunkManager chunks)
    {
        if (deltaTime <= 0f) return _age < _lifetime;
        _age += deltaTime;
        if (_age >= _lifetime) return false;

        var (fluidX, fluidY, fluidZ) = Position.BlockCell();
        var submerged = Position.Y < chunks.GetFluidSurfaceGlobal(fluidX, fluidY, fluidZ);
        _velocity.Y = BodyMotion.SinkOrFall(_velocity.Y, submerged, deltaTime);

        var movement = _velocity * deltaTime;
        var half = new Vector3(Size * 0.5f);
        Position = BoxSweep.MoveOnAxis(Position, movement.X, 0, half, chunks, out var hitX);
        Position = BoxSweep.MoveOnAxis(Position, movement.Z, 2, half, chunks, out var hitZ);
        Position = BoxSweep.MoveOnAxis(Position, movement.Y, 1, half, chunks, out var hitY);
        if (hitX) _velocity.X = 0f;
        if (hitZ) _velocity.Z = 0f;

        var grounded = hitY && _velocity.Y < 0f;
        if (hitY)
            _velocity.Y = grounded
                ? BodyMotion.Bounce(-_velocity.Y, submerged, BodyMotion.HeavyBounceThreshold, BodyMotion.HeavyBounceDamping)
                : 0f;

        Orientation = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(_tumbleAxis, Spin(grounded, deltaTime) * deltaTime)
                                           * Orientation);
        return true;
    }

    // The rate it turns at this frame: on the ground it rolls the way it slides (about the horizontal axis across its
    // heading, as fast as its rim moves) while friction slows it; in the air it keeps its own tumble
    private float Spin(bool grounded, float deltaTime)
    {
        if (!grounded) return _tumbleRate;
        var scrub = MathF.Exp(-JuiceConfig.TowerDebrisRollFriction * deltaTime);
        _velocity.X *= scrub;
        _velocity.Z *= scrub;
        var slide = new Vector3(_velocity.X, 0f, _velocity.Z);
        var speed = slide.Length();
        if (speed < 0.05f) return 0f;
        _tumbleAxis = Vector3.Cross(Vector3.UnitY, slide / speed);
        return speed / (Size * 0.5f);
    }
}
