using System;
using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Live fire particles: embers and smoke rising off every lit cell the caller feeds in. The
/// default instance is the campfire/kiln tuning; exposed lava runs a second instance with its
/// own, much sparser rates and cap so the two sources never compete for spawns or ring slots.
/// Main thread only.
/// </summary>
public sealed class FireParticleSystem
{
    /// <summary>
    /// Where the flame of a lit cell sits inside it: centred, a little above the floor. A caller
    /// whose flame is not at a cell's own origin (the kiln, whose fire burns inside its hearth)
    /// subtracts this from the point it wants so the particles rise from exactly there.
    /// Deliberately not the same as <see cref="RenderManager.FireLightOffset"/>, which lifts the
    /// point light well above the flame.
    /// </summary>
    public static readonly Vector3 SpawnOffset = new(0.5f, 0.35f, 0.5f);

    private readonly ParticleRing<FireParticle> _particles;
    private readonly float _embersPerSecond;
    private readonly float _smokePerSecond;
    private readonly int _maxParticles;
    private readonly Vector3 _spawnOffset;
    private readonly float _spawnJitter;
    private readonly float _particleScale;
    private float _emberDebt;
    private float _smokeDebt;

    /// <summary>The campfire tuning: <see cref="WorldGenerationConfig.FireEmberPerSecond"/>,
    /// <see cref="WorldGenerationConfig.FireSmokePerSecond"/>, <see cref="WorldGenerationConfig.FireParticleMax"/>
    /// and <see cref="SpawnOffset"/>.</summary>
    public FireParticleSystem()
        : this(WorldGenerationConfig.FireEmberPerSecond, WorldGenerationConfig.FireSmokePerSecond,
            WorldGenerationConfig.FireParticleMax, SpawnOffset)
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
        float spawnJitter = 0.5f, float particleScale = 1f)
    {
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
            if (dx * dx + dz * dz > WorldGenerationConfig.FireParticleRadius * WorldGenerationConfig.FireParticleRadius)
                continue;
            _emberDebt += deltaTime * _embersPerSecond;
            _smokeDebt += deltaTime * _smokePerSecond;
            var origin = cell + _spawnOffset;
            while (_emberDebt >= 1f)
            {
                _emberDebt -= 1f;
                Spawn(origin, FireParticleKind.Ember, rng);
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
