using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// One chunk of mining debris: a small cube textured as the block it broke off from.
/// Pops out on a mining hit, falls under gravity, bounces off the terrain and fades out.
/// Pure CPU state — <see cref="Systems.RenderManager"/> draws the live set through the
/// shared <see cref="BlockCubeCache"/>. Main thread only, like every other entity.
/// </summary>
public class BlockDebris
{
    /// <summary>Block the cube renders as (see <see cref="BlockCubeCache"/>).</summary>
    public byte BlockId { get; }

    /// <summary>
    /// The plain path's <c>uBiomeTint</c> for this cube: a biome-tinted block's tint sampled at
    /// the cell it chipped off (<see cref="BiomeTint.CellTint"/>), so the chips match the
    /// terrain they fly out of; white for every other block.
    /// </summary>
    public Vector3 Tint { get; }

    public Vector3 Position { get; private set; }

    /// <summary>Edge length of the cube, in blocks.</summary>
    public float Size { get; }

    /// <summary>Spin about Y, in radians.</summary>
    public float Spin { get; private set; }

    /// <summary>Tumble about X, in radians.</summary>
    public float Tumble { get; private set; }

    /// <summary>Whether the debris has come to rest and is fading out.</summary>
    public bool IsLanded { get; private set; }

    /// <summary>Whether the debris is still alive (false once faded or timed out).</summary>
    public bool IsAlive { get; private set; } = true;

    /// <summary>Fade multiplier 0..1: snaps in at spawn, eases out at end of life or after landing.</summary>
    public float Alpha
    {
        get
        {
            var fadeIn = MathUtils.Clamp01(_age / FadeInSeconds);
            var fadeOut = 1f - MathUtils.Clamp01(
                (_age - (_lifetime - WorldGenerationConfig.BlockDebrisFadeSeconds))
                / WorldGenerationConfig.BlockDebrisFadeSeconds);
            if (IsLanded)
                fadeOut = MathF.Min(fadeOut,
                    1f - MathUtils.Clamp01(_landAge / WorldGenerationConfig.BlockDebrisFadeSeconds));
            return MathF.Min(fadeIn, fadeOut);
        }
    }

    private const float FadeInSeconds = 0.08f;

    private const float Gravity = 28f;
    private const float TerminalVelocity = -30f;
    private const float GroundFriction = 8f;
    private const float BounceDamping = 0.35f;
    private const float BounceThreshold = 4f;
    private const float CollisionStepSize = 0.05f;

    private const float WaterSinkSpeed = -3f;
    private const float WaterDrag = 5f;

    private Vector3 _velocity;
    private float _age;
    private readonly float _lifetime;
    private float _landAge;
    private readonly float _spinSpeed;
    private readonly float _tumbleSpeed;

    private float HalfExtent => Size * 0.5f;

    public BlockDebris(byte blockId, Vector3 position, Vector3? toss = null, float? size = null, Random? random = null,
        Vector3? tint = null)
    {
        var rng = random ?? Random.Shared;
        BlockId = blockId;
        Tint = tint ?? Vector3.One;
        Position = position;
        Size = size ?? WorldGenerationConfig.BlockDebrisSize * (0.7f + 0.6f * rng.NextSingle());
        _lifetime = WorldGenerationConfig.BlockDebrisLifetime * (0.7f + 0.6f * rng.NextSingle());

        Spin = rng.NextSingle() * MathF.Tau;
        Tumble = rng.NextSingle() * MathF.Tau;
        _spinSpeed = (rng.NextSingle() * 2f - 1f) * 6f;
        _tumbleSpeed = (rng.NextSingle() * 2f - 1f) * 6f;

        var angle = rng.NextSingle() * MathF.Tau;
        var horizontalSpeed = 0.8f + rng.NextSingle() * 1.8f;
        _velocity = new Vector3(
            MathF.Cos(angle) * horizontalSpeed,
            2f + rng.NextSingle() * 2f,
            MathF.Sin(angle) * horizontalSpeed);
        if (toss.HasValue)
            _velocity += toss.Value;
    }

    /// <summary>
    /// Advances the debris. Returns false once it should be removed.
    /// Falls under gravity, collides axis by axis like the item drops, bounces light
    /// impacts and rests on hard ones, then fades in place. Sinks slowly through water.
    /// </summary>
    public bool Update(float deltaTime, ChunkManager chunks)
    {
        if (!IsAlive) return false;
        if (deltaTime <= 0f || chunks == null) return true;

        _age += deltaTime;
        if (_age >= _lifetime)
        {
            IsAlive = false;
            return false;
        }

        if (IsLanded)
        {
            _landAge += deltaTime;
            Spin += _spinSpeed * 0.2f * deltaTime;
            if (_landAge >= WorldGenerationConfig.BlockDebrisFadeSeconds)
            {
                IsAlive = false;
                return false;
            }
            return true;
        }

        var (fluidX, fluidY, fluidZ) = Position.BlockCell();
        var submerged = Position.Y < chunks.GetFluidSurfaceGlobal(fluidX, fluidY, fluidZ);

        if (submerged)
            _velocity.Y = MathUtils.Damp(_velocity.Y, WaterSinkSpeed, WaterDrag, deltaTime);
        else
            _velocity.Y = MathF.Max(_velocity.Y - Gravity * deltaTime, TerminalVelocity);

        var movement = _velocity * deltaTime;

        Position = MoveOnAxis(Position, movement.X, 0, chunks, out var hitX);
        Position = MoveOnAxis(Position, movement.Z, 2, chunks, out var hitZ);
        Position = MoveOnAxis(Position, movement.Y, 1, chunks, out var hitY);

        if (hitX) _velocity.X = 0f;
        if (hitZ) _velocity.Z = 0f;

        if (hitY)
        {
            if (_velocity.Y < 0f)
            {
                var impact = -_velocity.Y;
                _velocity.Y = !submerged && impact >= BounceThreshold ? impact * BounceDamping : 0f;
                if (_velocity.Y == 0f)
                {
                    IsLanded = true;
                    _landAge = 0f;
                }
            }
            else
            {
                _velocity.Y = 0f;
            }
        }

        if (IsLanded)
        {
            var damp = MathF.Max(0f, 1f - GroundFriction * deltaTime);
            _velocity.X *= damp;
            _velocity.Z *= damp;
        }

        Spin += _spinSpeed * deltaTime;
        Tumble += _tumbleSpeed * deltaTime;

        return true;
    }

    private Vector3 MoveOnAxis(Vector3 start, float delta, int axis, ChunkManager chunks, out bool collided)
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

            if (IsColliding(candidate, chunks))
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

    private bool IsColliding(Vector3 center, ChunkManager chunks)
    {
        var half = HalfExtent;
        VoxelCoords.CellRange(center, half, out var minX, out var minY, out var minZ, out var maxX, out var maxY, out var maxZ);

        for (var x = minX; x <= maxX; x++)
        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
        {
            if (BlockType.IsSolid(chunks.GetBlockGlobal(x, y, z)))
                return true;
        }

        return false;
    }
}
