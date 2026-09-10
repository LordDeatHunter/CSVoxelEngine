using System;
using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Live campfire particles: embers and smoke rising off every lit cell the caller feeds in.
/// Main thread only.
/// </summary>
public sealed class FireParticleSystem
{
    private readonly List<FireParticle> _particles = [];
    private float _emberDebt;
    private float _smokeDebt;

    public IReadOnlyList<FireParticle> Particles => _particles;

    internal void AddForTest(FireParticle particle) => _particles.Add(particle);

    public void Update(float deltaTime, IReadOnlyList<Vector3> litCells, Vector3 cameraPos, Random? random = null)
    {
        if (deltaTime <= 0f) return;
        var rng = random ?? Random.Shared;
        foreach (var cell in litCells)
        {
            var dx = cell.X + 0.5f - cameraPos.X;
            var dz = cell.Z + 0.5f - cameraPos.Z;
            if (dx * dx + dz * dz > WorldGenerationConfig.FireParticleRadius * WorldGenerationConfig.FireParticleRadius)
                continue;
            _emberDebt += deltaTime * WorldGenerationConfig.FireEmberPerSecond;
            _smokeDebt += deltaTime * WorldGenerationConfig.FireSmokePerSecond;
            var origin = cell + new Vector3(0.5f, 0.35f, 0.5f);
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
            if (!_particles[i].Update(deltaTime))
                _particles.RemoveAt(i);
        }
    }

    private void Spawn(Vector3 origin, FireParticleKind kind, Random rng)
    {
        if (_particles.Count >= WorldGenerationConfig.FireParticleMax)
            _particles.RemoveAt(0);
        var jitter = new Vector3(
            (rng.NextSingle() - 0.5f) * 0.5f, rng.NextSingle() * 0.15f, (rng.NextSingle() - 0.5f) * 0.5f);
        _particles.Add(new FireParticle(origin + jitter, kind, rng));
    }
}
