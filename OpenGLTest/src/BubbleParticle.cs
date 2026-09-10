using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// One exhaled bubble: a small billboard that rises off the player's mouth while the eye
/// is submerged, wobbles sideways as it climbs, and pops the moment it reaches the water
/// surface (or when its time runs out). Pure CPU state; drawn batched by
/// <see cref="BubbleParticleRenderer"/>. Main thread only, like every other entity.
/// </summary>
public sealed class BubbleParticle
{
    public Vector3 Position { get; private set; }
    public float Size { get; private set; }
    public bool IsAlive { get; private set; } = true;

    /// <summary>Fade multiplier 0..1: eases in at spawn, eases out at end of life.</summary>
    public float Alpha
    {
        get
        {
            var fadeIn = MathUtils.Clamp01(_age / FadeInSeconds);
            var fadeOut = 1f - MathUtils.Clamp01(
                (_age - (_lifetime - WorldGenerationConfig.BubbleFadeSeconds))
                / WorldGenerationConfig.BubbleFadeSeconds);
            return MathF.Min(fadeIn, fadeOut);
        }
    }

    private const float FadeInSeconds = 0.15f;

    private Vector3 _velocity;
    private float _age;
    private readonly float _lifetime;
    private readonly float _grow;
    private readonly float _wobblePhase;
    private readonly float _wobbleFreq;
    private readonly float _wobbleAmp;

    public BubbleParticle(Vector3 position, Random? random = null)
    {
        var rng = random ?? Random.Shared;
        Position = position;
        Size = WorldGenerationConfig.BubbleSize * (0.7f + 0.6f * rng.NextSingle());
        _lifetime = WorldGenerationConfig.BubbleLifetime * (0.7f + 0.6f * rng.NextSingle());
        _grow = WorldGenerationConfig.BubbleGrow;
        _velocity = new Vector3(
            (rng.NextSingle() * 2f - 1f) * 0.2f,
            WorldGenerationConfig.BubbleRise * (0.8f + 0.4f * rng.NextSingle()),
            (rng.NextSingle() * 2f - 1f) * 0.2f);
        _wobblePhase = rng.NextSingle() * MathF.Tau;
        _wobbleFreq = 2f + rng.NextSingle() * 2f;
        _wobbleAmp = 0.25f + rng.NextSingle() * 0.25f;
    }

    /// <summary>
    /// Advances the bubble. Returns false once it should be removed: on timeout, or the
    /// moment its centre reaches the fluid surface above it (popping out of the water).
    /// A bubble found in a cell with no fluid has already left the water, so it pops too.
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

        _velocity.Y = MathUtils.Damp(_velocity.Y,
            WorldGenerationConfig.BubbleRise,
            WorldGenerationConfig.BubbleDrag, deltaTime);

        Position += _velocity * deltaTime;
        Position += new Vector3(
            MathF.Sin(_age * _wobbleFreq + _wobblePhase) * _wobbleAmp * deltaTime,
            0f,
            MathF.Cos(_age * _wobbleFreq * 0.8f + _wobblePhase) * _wobbleAmp * deltaTime);
        Size += _grow * deltaTime;

        var surface = chunks.GetFluidSurfaceGlobal(
            (int)MathF.Floor(Position.X),
            (int)MathF.Floor(Position.Y),
            (int)MathF.Floor(Position.Z));
        if (Position.Y >= surface)
        {
            IsAlive = false;
            return false;
        }

        return true;
    }
}
