using System;
using System.Numerics;

namespace silkgltriangle;

public enum FireParticleKind : byte
{
    Ember,
    Smoke,
}

/// <summary>
/// One campfire particle: an ember (small, orange, fast rise, short life) or a smoke puff
/// (larger, grey, slow rise, grows as it climbs). Pure CPU state; drawn batched by
/// <see cref="FireParticleRenderer"/>. Main thread only.
/// </summary>
public sealed class FireParticle
{
    public Vector3 Position { get; private set; }
    public FireParticleKind Kind { get; }
    public float Size { get; private set; }
    public bool IsAlive { get; private set; } = true;

    public float Alpha => Kind == FireParticleKind.Ember
        ? 1f - MathUtils.Clamp01(_age / _lifetime)
        : 0.45f * (1f - MathUtils.Clamp01(_age / _lifetime));

    public (byte R, byte G, byte B) Color => Kind == FireParticleKind.Ember
        ? EmberColor(_age / _lifetime)
        : ((byte)110, (byte)110, (byte)115);

    private Vector3 _velocity;
    private float _age;
    private readonly float _lifetime;
    private readonly float _grow;
    private readonly float _swayPhase;
    private readonly float _swayAmp;

    public FireParticle(Vector3 position, FireParticleKind kind, Random? random = null)
    {
        var rng = random ?? Random.Shared;
        Position = position;
        Kind = kind;
        if (kind == FireParticleKind.Ember)
        {
            _lifetime = WorldGenerationConfig.FireEmberLifetime * (0.6f + 0.8f * rng.NextSingle());
            _velocity = new Vector3(
                (rng.NextSingle() * 2f - 1f) * 0.35f,
                WorldGenerationConfig.FireEmberRise * (0.8f + 0.4f * rng.NextSingle()),
                (rng.NextSingle() * 2f - 1f) * 0.35f);
            Size = WorldGenerationConfig.FireEmberSize * (0.7f + 0.6f * rng.NextSingle());
            _grow = 0f;
        }
        else
        {
            _lifetime = WorldGenerationConfig.FireSmokeLifetime * (0.7f + 0.6f * rng.NextSingle());
            _velocity = new Vector3(
                (rng.NextSingle() * 2f - 1f) * 0.2f,
                WorldGenerationConfig.FireSmokeRise * (0.85f + 0.3f * rng.NextSingle()),
                (rng.NextSingle() * 2f - 1f) * 0.2f);
            Size = WorldGenerationConfig.FireSmokeSize * (0.8f + 0.4f * rng.NextSingle());
            _grow = WorldGenerationConfig.FireSmokeGrow;
        }
        _swayPhase = rng.NextSingle() * MathF.Tau;
        _swayAmp = kind == FireParticleKind.Ember ? 0.35f : 0.15f;
    }

    public bool Update(float deltaTime)
    {
        if (!IsAlive) return false;
        if (deltaTime <= 0f) return true;
        _age += deltaTime;
        if (_age >= _lifetime)
        {
            IsAlive = false;
            return false;
        }
        Position += _velocity * deltaTime;
        Position += new Vector3(MathF.Sin(_age * 3f + _swayPhase) * _swayAmp * deltaTime, 0f, 0f);
        Size += _grow * deltaTime;
        return true;
    }

    private static (byte R, byte G, byte B) EmberColor(float t) => t switch
    {
        < 0.4f => ((byte)255, (byte)180, (byte)60),
        < 0.75f => ((byte)255, (byte)110, (byte)25),
        _ => ((byte)200, (byte)40, (byte)10),
    };
}
