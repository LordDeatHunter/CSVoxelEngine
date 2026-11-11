using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Rendering;

namespace VoxelEngine.Particles;

/// <summary>
/// Live fire particles: embers and smoke rising off every lit cell the caller feeds in. The
/// default instance is the campfire/kiln tuning; exposed lava runs a second instance with its
/// own, much sparser rates and cap so the two sources never compete for spawns or ring slots.
/// Main thread only.
/// </summary>
public sealed class FireParticleSystem
{
    /// <summary>
    /// Where the flame of a lit cell sits inside it: centered, a little above the floor. A caller
    /// whose flame is not at a cell's own origin (the kiln, whose fire burns inside its hearth)
    /// subtracts this from the point it wants so the particles rise from exactly there.
    /// Deliberately not the same as <see cref="FrameLighting.FireLightOffset"/>, which lifts the
    /// point light well above the flame.
    /// </summary>
    public static readonly Vector3 SpawnOffset = new(0.5f, 0.35f, 0.5f);

    private readonly ParticleRing<FireParticle> _particles;
    private readonly float _embersPerSecond;
    private readonly float _smokePerSecond;
    private readonly float _flamesPerSecond;
    private readonly int _maxParticles;
    private readonly Vector3 _spawnOffset;
    private readonly float _spawnJitter;
    private readonly float _particleScale;
    private float _emberDebt;
    private float _smokeDebt;
    private float _flameDebt;

    /// <summary>The campfire tuning: <see cref="ParticleConfig.FireEmberPerSecond"/>,
    /// <see cref="ParticleConfig.FireSmokePerSecond"/>, <see cref="ParticleConfig.FireParticleMax"/>
    /// and <see cref="SpawnOffset"/>.</summary>
    public FireParticleSystem()
        : this(ParticleConfig.FireEmberPerSecond, ParticleConfig.FireSmokePerSecond,
            ParticleConfig.FireParticleMax, SpawnOffset, flamesPerSecond: ParticleConfig.FireFlamePerSecond)
    {
    }

    /// <summary>
    /// A separately tuned system: <paramref name="embersPerSecond"/> and <paramref name="smokePerSecond"/>
    /// are per lit cell in range, <paramref name="maxParticles"/> is this instance's own ring
    /// capacity (the oldest particle is evicted past it) and <paramref name="spawnOffset"/> is
    /// where inside a cell the particles start. <paramref name="spawnJitter"/> is how wide the spawn
    /// scatter is across the origin (a firepit's half block by default; a torch's flame point is
    /// exact, so its scatter is a pixel or two) and <paramref name="particleScale"/> shrinks every
    /// particle it spawns (<see cref="FireParticle"/>'s scale).
    /// </summary>
    public FireParticleSystem(float embersPerSecond, float smokePerSecond, int maxParticles, Vector3 spawnOffset,
        float spawnJitter = 0.5f, float particleScale = 1f, float flamesPerSecond = 0f)
    {
        _flamesPerSecond = flamesPerSecond;
        _embersPerSecond = embersPerSecond;
        _smokePerSecond = smokePerSecond;
        _maxParticles = maxParticles;
        _spawnOffset = spawnOffset;
        _spawnJitter = spawnJitter;
        _particleScale = particleScale;
        _particles = new ParticleRing<FireParticle>(maxParticles);
    }

    public IReadOnlyList<FireParticle> Particles => _particles;

    internal void AddForTest(FireParticle particle) => _particles.Add(particle);

    /// <summary>Spawns for the lit cells in range and advances every particle, drifting them along <paramref name="wind"/> (see <see cref="FireParticle.Update"/>).</summary>
    public void Update(float deltaTime, IReadOnlyList<Vector3> litCells, Vector3 cameraPos, Random? random = null,
        Vector2 wind = default)
    {
        if (deltaTime <= 0f) return;
        var rng = random ?? Random.Shared;
        foreach (var cell in litCells)
        {
            var dx = cell.X + 0.5f - cameraPos.X;
            var dz = cell.Z + 0.5f - cameraPos.Z;
            if (dx * dx + dz * dz > ParticleConfig.FireParticleRadius * ParticleConfig.FireParticleRadius)
                continue;
            _emberDebt += deltaTime * _embersPerSecond;
            _smokeDebt += deltaTime * _smokePerSecond;
            _flameDebt += deltaTime * _flamesPerSecond;
            var origin = cell + _spawnOffset;
            while (_emberDebt >= 1f)
            {
                _emberDebt -= 1f;
                Spawn(origin, FireParticleKind.Ember, rng);
            }
            while (_flameDebt >= 1f)
            {
                _flameDebt -= 1f;
                SpawnFlame(origin, rng);
            }
            while (_smokeDebt >= 1f)
            {
                _smokeDebt -= 1f;
                Spawn(origin, FireParticleKind.Smoke, rng);
            }
        }
        if (litCells.Count == 0)
        {
            _emberDebt = 0f;
            _smokeDebt = 0f;
            _flameDebt = 0f;
        }
        for (var i = _particles.Count - 1; i >= 0; i--)
        {
            if (!_particles[i].Update(deltaTime, wind))
                _particles.RemoveAt(i);
        }
    }

    /// <summary>
    /// A one-off burst of <paramref name="count"/> puffs of <paramref name="kind"/> rising from around
    /// <paramref name="origin"/>, scattered up to half of <paramref name="spread"/> either way across
    /// it (the steam off lava that just hardened). Evicts the oldest particles past the cap like any
    /// spawn; the next <see cref="Update"/> moves them.
    /// </summary>
    public void Puff(Vector3 origin, int count, float spread, FireParticleKind kind = FireParticleKind.Smoke,
        Random? random = null)
    {
        var rng = random ?? Random.Shared;
        for (var i = 0; i < count; i++)
        {
            if (_particles.Count >= _maxParticles)
                _particles.RemoveAt(0);
            var jitter = new Vector3((rng.NextSingle() - 0.5f) * spread, rng.NextSingle() * 0.2f,
                (rng.NextSingle() - 0.5f) * spread);
            _particles.Add(new FireParticle(origin + jitter, kind, rng));
        }
    }

    /// <summary>
    /// One cylinder of <see cref="FireParticleKind.Pulse"/> motes leaving the vertical axis through
    /// <paramref name="center"/> flat in every direction at <paramref name="speed"/> blocks per second: a stack of layers
    /// <paramref name="layerSpacing"/> apart from <paramref name="bottomY"/> to <paramref name="topY"/>, each of
    /// <paramref name="perLayer"/> motes evenly spaced around the circle from its own random start, so the shell covers
    /// the whole height and its layers do not line up. Evicts the oldest particles past the cap like any spawn.
    /// </summary>
    public void PulseCylinder(Vector3 center, float bottomY, float topY, int perLayer, float layerSpacing, float speed,
        float lifetime, float size, Random? random = null)
    {
        var rng = random ?? Random.Shared;
        var layers = Math.Max(1, (int)MathF.Ceiling((topY - bottomY) / layerSpacing));
        for (var layer = 0; layer < layers; layer++)
        {
            var y = bottomY + (topY - bottomY) * (layer + 0.5f) / layers;
            var start = rng.NextSingle() * MathF.Tau;
            for (var i = 0; i < perLayer; i++)
            {
                if (_particles.Count >= _maxParticles)
                    _particles.RemoveAt(0);
                var angle = start + MathF.Tau * i / perLayer;
                var velocity = new Vector3(MathF.Cos(angle) * speed, 0f, MathF.Sin(angle) * speed);
                _particles.Add(new FireParticle(new Vector3(center.X, y, center.Z), velocity, lifetime, size));
            }
        }
    }

    // A flame starts low and near the middle of the fire, so the tongues cluster into one body of flame
    private void SpawnFlame(Vector3 origin, Random rng)
    {
        if (_particles.Count >= _maxParticles)
            _particles.RemoveAt(0);
        var spread = _spawnJitter * ParticleConfig.FireFlameSpreadShare;
        var jitter = new Vector3((rng.NextSingle() - 0.5f) * spread, -0.05f, (rng.NextSingle() - 0.5f) * spread);
        _particles.Add(new FireParticle(origin + jitter, FireParticleKind.Flame, rng, _particleScale));
    }

    private void Spawn(Vector3 origin, FireParticleKind kind, Random rng)
    {
        if (_particles.Count >= _maxParticles)
            _particles.RemoveAt(0);
        var jitter = new Vector3(
            (rng.NextSingle() - 0.5f) * _spawnJitter, rng.NextSingle() * 0.3f * _spawnJitter,
            (rng.NextSingle() - 0.5f) * _spawnJitter);
        _particles.Add(new FireParticle(origin + jitter, kind, rng, _particleScale));
    }
}
