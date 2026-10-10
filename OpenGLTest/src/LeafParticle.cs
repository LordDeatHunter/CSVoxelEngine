using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// One falling leaf: a small billboard that flutters down off a canopy with a sinusoidal
/// sway and a slow tumble, then rests where it lands and fades out. Pure CPU state —
/// <see cref="LeafParticleRenderer"/> draws the live set in one batched draw.
/// Main thread only, like every other entity.
/// </summary>
public class LeafParticle
{
    public Vector3 Position { get; private set; }

    /// <summary>Planar spin of the billboard, in radians.</summary>
    public float Rotation { get; private set; }

    /// <summary>Edge length of the billboard quad, in blocks.</summary>
    public float Size { get; }

    /// <summary>Whether the leaf has come to rest on the terrain and is fading out.</summary>
    public bool IsLanded { get; private set; }

    /// <summary>Whether the leaf is still alive (false once fully faded or timed out).</summary>
    public bool IsAlive { get; private set; } = true;

    /// <summary>Fade multiplier 0..1: eases in at spawn, eases out once landed or expiring.</summary>
    public float Alpha
    {
        get
        {
            var fadeIn = MathUtils.Clamp01(_age / FadeInSeconds);
            var fadeOut = 1f;
            if (IsLanded)
                fadeOut = 1f - MathUtils.Clamp01(_landAge / WorldGenerationConfig.LeafParticleFadeSeconds);
            else
                fadeOut = 1f - MathUtils.Clamp01(
                    (_age - (_lifetime - WorldGenerationConfig.LeafParticleFadeSeconds))
                    / WorldGenerationConfig.LeafParticleFadeSeconds);
            return MathF.Min(fadeIn, fadeOut);
        }
    }

    private const float FadeInSeconds = 0.2f;

    private Vector3 _velocity;
    private float _age;
    private readonly float _lifetime;
    private float _landAge;
    private readonly float _spin;
    private readonly float _swayPhase;
    private readonly float _swayFreq;
    private readonly float _swayAmp;

    public LeafParticle(Vector3 position, Vector3? toss = null, float? size = null, Random? random = null)
    {
        var rng = random ?? Random.Shared;
        Position = position;
        Size = size ?? WorldGenerationConfig.LeafParticleSize;
        _lifetime = WorldGenerationConfig.LeafParticleLifetime * (0.7f + 0.6f * rng.NextSingle());

        Rotation = rng.NextSingle() * MathF.Tau;
        _spin = (rng.NextSingle() * 2f - 1f) * 2.5f;
        _swayPhase = rng.NextSingle() * MathF.Tau;
        _swayFreq = 1.5f + rng.NextSingle() * 1.5f;
        _swayAmp = 0.4f + rng.NextSingle() * 0.5f;

        _velocity = new Vector3(
            (rng.NextSingle() * 2f - 1f) * 0.5f,
            -0.2f - rng.NextSingle() * 0.4f,
            (rng.NextSingle() * 2f - 1f) * 0.5f);
        if (toss.HasValue)
            _velocity += toss.Value;
    }

    /// <summary>
    /// Advances the leaf. Returns false once it should be removed.
    /// A leaf sinks toward its flutter speed instead of accelerating, drifts sideways on
    /// its sway, and dies on a solid cell below it (fading in place) or when its time runs out.
    /// Submerged leaves drown fast instead of piling up on the sea floor. <paramref name="wind"/> is
    /// the unit downwind direction (<see cref="Wind.Direction"/>) and <paramref name="gust"/> how hard
    /// it blows here (<see cref="Wind.Gust"/>): a falling leaf's sideways motion settles into the
    /// wind's, while a landed one stays put. Left at default, the leaf falls in still air.
    /// </summary>
    public bool Update(float deltaTime, ChunkManager chunks, Vector2 wind = default, float gust = 1f)
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
            if (_landAge >= WorldGenerationConfig.LeafParticleFadeSeconds)
            {
                IsAlive = false;
                return false;
            }
            return true;
        }

        var (cellX, cellY, cellZ) = Position.BlockCell();
        if (Position.Y < chunks.GetFluidSurfaceGlobal(cellX, cellY, cellZ))
        {
            IsAlive = false;
            return false;
        }

        _velocity.Y = MathUtils.Damp(_velocity.Y,
            -WorldGenerationConfig.LeafParticleFallSpeed,
            WorldGenerationConfig.LeafParticleFallDrag, deltaTime);

        if (wind != default)
        {
            // A leaf is all drag: its sideways motion (a burst's toss included) eases into the wind's
            var drift = wind * (WorldGenerationConfig.WindLeafDrift * gust);
            _velocity.X = MathUtils.Damp(_velocity.X, drift.X, WorldGenerationConfig.WindLeafDrag, deltaTime);
            _velocity.Z = MathUtils.Damp(_velocity.Z, drift.Y, WorldGenerationConfig.WindLeafDrag, deltaTime);
        }

        Position += _velocity * deltaTime;
        Position += new Vector3(
            MathF.Sin(_age * _swayFreq + _swayPhase) * _swayAmp * deltaTime,
            0f,
            MathF.Cos(_age * _swayFreq * 0.8f + _swayPhase) * _swayAmp * deltaTime);
        Rotation += _spin * deltaTime;

        var footY = Position.Y - Size * 0.5f;
        if (BlockType.IsSolid(chunks.GetBlockGlobal(cellX, MathUtils.FloorToInt(footY), cellZ)))
        {
            IsLanded = true;
            _landAge = 0f;
        }

        return true;
    }
}
