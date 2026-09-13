using System;
using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Live exhaled bubbles: spawns them at the player's mouth while the eye is submerged and
/// advances every bubble until it pops at the surface or times out. Main thread only.
/// </summary>
public sealed class BubbleParticleSystem
{
    private readonly ParticleRing<BubbleParticle> _particles = new(WorldGenerationConfig.BubbleParticleMax);
    private float _spawnDebt;

    public IReadOnlyList<BubbleParticle> Particles => _particles;

    internal void AddForTest(BubbleParticle particle) => _particles.Add(particle);

    /// <summary>
    /// Spawns bubbles at the mouth (eye pushed forward and down a touch) while
    /// <paramref name="underwater"/> holds, then advances every bubble, removing the dead.
    /// </summary>
    public void Update(float deltaTime, Vector3 eyePos, Vector3 forward, bool underwater,
        ChunkManager chunks, Random? random = null)
    {
        if (deltaTime <= 0f || chunks == null) return;
        var rng = random ?? Random.Shared;

        if (underwater)
        {
            var f = forward.LengthSquared() > 1e-6f ? Vector3.Normalize(forward) : -Vector3.UnitZ;
            var mouth = eyePos + f * 0.3f - Vector3.UnitY * 0.18f;
            _spawnDebt += deltaTime * WorldGenerationConfig.BubblePerSecond;
            while (_spawnDebt >= 1f)
            {
                _spawnDebt -= 1f;
                Spawn(mouth, rng);
            }
        }
        else
        {
            _spawnDebt = 0f;
        }

        for (var i = _particles.Count - 1; i >= 0; i--)
        {
            if (!_particles[i].Update(deltaTime, chunks))
                _particles.RemoveAt(i);
        }
    }

    private void Spawn(Vector3 mouth, Random rng)
    {
        if (_particles.Count >= WorldGenerationConfig.BubbleParticleMax)
            _particles.RemoveAt(0);
        var jitter = new Vector3(
            (rng.NextSingle() - 0.5f) * 0.12f,
            (rng.NextSingle() - 0.5f) * 0.08f,
            (rng.NextSingle() - 0.5f) * 0.12f);
        _particles.Add(new BubbleParticle(mouth + jitter, rng));
    }
}
