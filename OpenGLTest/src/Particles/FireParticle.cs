using System;
using System.Numerics;
using VoxelEngine.Effects.Sky;

namespace VoxelEngine.Particles;

public enum FireParticleKind : byte
{
    Ember,
    Smoke,

    /// <summary>A pale puff off lava that just hardened: drawn like smoke (blended, unlit) but
    /// lighter, quicker to rise and swell, and shorter lived.</summary>
    Steam,

    /// <summary>A bright mote where a drop was just collected: drawn like smoke but small, quick
    /// to rise, warm and brief.</summary>
    Spark,

    /// <summary>A tongue of flame: drawn additively like an ember but as a tall soft teardrop, short lived,
    /// rising while it shrinks and cools from white-yellow through orange to red.</summary>
    Flame,

    /// <summary>A glowing mote of a conquered battle-tower floor's pulse: drawn additively like an ember, flung
    /// flat and outward from the floor's center at a fixed speed, fading as it travels.</summary>
    Pulse,
}

/// <summary>
/// One campfire particle: an ember (small, orange, fast rise, short life), a smoke puff
/// (larger, gray, slow rise, grows as it climbs) or a steam puff (pale, quick, brief). Pure CPU
/// state; drawn batched by <see cref="FireParticleRenderer"/>, which blends everything but an
/// ember like smoke. Main thread only.
/// </summary>
public sealed class FireParticle
{
    public Vector3 Position { get; private set; }
    public FireParticleKind Kind { get; }
    public float Size { get; private set; }
    public bool IsAlive { get; private set; } = true;

    public float Alpha => Kind switch
    {
        FireParticleKind.Ember => 1f - MathUtils.Clamp01(_age / _lifetime),
        FireParticleKind.Steam => JuiceConfig.SteamAlpha * (1f - MathUtils.Clamp01(_age / _lifetime)),
        FireParticleKind.Spark => 0.9f * (1f - MathUtils.Clamp01(_age / _lifetime)),
        FireParticleKind.Flame => 0.5f * (1f - MathUtils.Clamp01(_age / _lifetime) * MathUtils.Clamp01(_age / _lifetime)),
        FireParticleKind.Pulse => 0.9f * (1f - MathUtils.Clamp01(_age / _lifetime)),
        _ => 0.45f * (1f - MathUtils.Clamp01(_age / _lifetime)),
    };

    public (byte R, byte G, byte B) Color => Kind switch
    {
        FireParticleKind.Ember => EmberColor(_age / _lifetime),
        FireParticleKind.Steam => ((byte)236, (byte)238, (byte)242),
        FireParticleKind.Spark => ((byte)255, (byte)238, (byte)170),
        FireParticleKind.Flame => FlameColor(_age / _lifetime),
        FireParticleKind.Pulse => ((byte)255, (byte)226, (byte)140),
        _ => ((byte)110, (byte)110, (byte)115),
    };

    private Vector3 _velocity;
    private float _age;
    private float _lifetime;
    private float _grow;
    private readonly float _swayPhase;
    private float _swayAmp;

    /// <param name="position">Spawn point, in world blocks.</param>
    /// <param name="kind">Ember, smoke, steam or spark.</param>
    /// <param name="random">Randomness source; <see cref="Random.Shared"/> when null.</param>
    /// <param name="scale">Shrinks (or grows) the whole particle: its size, growth, lifetime, speed and
    /// sway all scale by it, so a torch's flame (a small scale) is a short, tight flicker over its head
    /// and its smoke a thin wisp, where a campfire's (1) fill a firepit.</param>
    public FireParticle(Vector3 position, FireParticleKind kind, Random? random = null, float scale = 1f)
        : this(position, kind, random ?? Random.Shared)
    {
        if (scale == 1f) return;
        Size *= scale;
        _grow *= scale;
        _lifetime *= scale;
        _velocity *= scale;
        _swayAmp *= scale;
    }

    /// <summary>A pulse mote (<see cref="FireParticleKind.Pulse"/>) launched from <paramref name="position"/> at
    /// <paramref name="velocity"/> (blocks per second), living <paramref name="lifetime"/> seconds.</summary>
    public FireParticle(Vector3 position, Vector3 velocity, float lifetime, float size)
    {
        Position = position;
        Kind = FireParticleKind.Pulse;
        _velocity = velocity;
        _lifetime = lifetime;
        Size = size;
    }

    private FireParticle(Vector3 position, FireParticleKind kind, Random random)
    {
        var rng = random;
        Position = position;
        Kind = kind;
        if (kind == FireParticleKind.Ember)
        {
            _lifetime = ParticleConfig.FireEmberLifetime * (0.6f + 0.8f * rng.NextSingle());
            _velocity = new Vector3(
                (rng.NextSingle() * 2f - 1f) * 0.35f,
                ParticleConfig.FireEmberRise * (0.8f + 0.4f * rng.NextSingle()),
                (rng.NextSingle() * 2f - 1f) * 0.35f);
            Size = ParticleConfig.FireEmberSize * (0.7f + 0.6f * rng.NextSingle());
            _grow = 0f;
        }
        else if (kind == FireParticleKind.Flame)
        {
            _lifetime = ParticleConfig.FireFlameLifetime * (0.7f + 0.6f * rng.NextSingle());
            _velocity = new Vector3(
                (rng.NextSingle() * 2f - 1f) * 0.06f,
                ParticleConfig.FireFlameRise * (0.8f + 0.4f * rng.NextSingle()),
                (rng.NextSingle() * 2f - 1f) * 0.06f);
            Size = ParticleConfig.FireFlameSize * (0.75f + 0.5f * rng.NextSingle());
            // Shrinks to a fifth of its size over its life, so the tip tapers out
            _grow = -Size * 0.8f / _lifetime;
        }
        else if (kind == FireParticleKind.Steam)
        {
            _lifetime = JuiceConfig.SteamLifetime * (0.7f + 0.6f * rng.NextSingle());
            _velocity = new Vector3(
                (rng.NextSingle() * 2f - 1f) * 0.3f,
                JuiceConfig.SteamRise * (0.8f + 0.4f * rng.NextSingle()),
                (rng.NextSingle() * 2f - 1f) * 0.3f);
            Size = JuiceConfig.SteamSize * (0.8f + 0.4f * rng.NextSingle());
            _grow = JuiceConfig.SteamGrow;
        }
        else if (kind == FireParticleKind.Spark)
        {
            _lifetime = JuiceConfig.SparkLifetime * (0.7f + 0.6f * rng.NextSingle());
            _velocity = new Vector3(
                (rng.NextSingle() * 2f - 1f) * 0.4f,
                JuiceConfig.SparkRise * (0.8f + 0.4f * rng.NextSingle()),
                (rng.NextSingle() * 2f - 1f) * 0.4f);
            Size = JuiceConfig.SparkSize * (0.8f + 0.4f * rng.NextSingle());
            _grow = 0f;
        }
        else
        {
            _lifetime = ParticleConfig.FireSmokeLifetime * (0.7f + 0.6f * rng.NextSingle());
            _velocity = new Vector3(
                (rng.NextSingle() * 2f - 1f) * 0.2f,
                ParticleConfig.FireSmokeRise * (0.85f + 0.3f * rng.NextSingle()),
                (rng.NextSingle() * 2f - 1f) * 0.2f);
            Size = ParticleConfig.FireSmokeSize * (0.8f + 0.4f * rng.NextSingle());
            _grow = ParticleConfig.FireSmokeGrow;
        }
        _swayPhase = rng.NextSingle() * MathF.Tau;
        _swayAmp = kind switch
        {
            FireParticleKind.Ember => 0.35f,
            FireParticleKind.Flame => 0.5f,
            _ => 0.15f,
        };
    }

    /// <summary>
    /// Advances the particle. <paramref name="wind"/> is the unit downwind direction
    /// (<see cref="Wind.Direction"/>); left at default the particle sways along X only, with no drift.
    /// </summary>
    public bool Update(float deltaTime, Vector2 wind = default)
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
        var sway = MathF.Sin(_age * 3f + _swayPhase) * _swayAmp * deltaTime;
        if (wind == default || Kind == FireParticleKind.Pulse)
        {
            Position += new Vector3(sway, 0f, 0f);
        }
        else
        {
            // Sway across the wind and drift down it. Smoke and steam pick the drift up as they rise
            // clear of their source (full after half their life); embers are carried from the start.
            var drift = Kind != FireParticleKind.Ember
                ? SkyConfig.WindSmokeDrift * MathUtils.Clamp01(_age / (0.5f * _lifetime))
                : SkyConfig.WindEmberDrift;
            Position += new Vector3(-wind.Y * sway + wind.X * drift * deltaTime, 0f,
                wind.X * sway + wind.Y * drift * deltaTime);
        }
        Size += _grow * deltaTime;
        return true;
    }

    private static (byte R, byte G, byte B) FlameColor(float t) => t switch
    {
        < 0.25f => ((byte)255, (byte)222, (byte)110),
        < 0.55f => ((byte)255, (byte)150, (byte)35),
        _ => ((byte)222, (byte)78, (byte)18),
    };

    private static (byte R, byte G, byte B) EmberColor(float t) => t switch
    {
        < 0.4f => ((byte)255, (byte)180, (byte)60),
        < 0.75f => ((byte)255, (byte)110, (byte)25),
        _ => ((byte)200, (byte)40, (byte)10),
    };
}
