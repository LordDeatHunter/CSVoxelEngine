using System;
using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Live exhaled bubbles: spawns them at the player's mouth while the eye is submerged, lets out a
/// burst from the head on each swim stroke, and advances every bubble until it pops at the surface
/// or times out. Main thread only.
/// </summary>
public sealed class BubbleParticleSystem
{
    private readonly ParticleRing<BubbleParticle> _particles = new(WorldGenerationConfig.BubbleParticleMax);
    private float _spawnDebt;
    // The swimmer's stroke counter as last seen, so a burst fires once per stroke
    private int _lastStrokes;
    private bool _strokesSeen;

    public IReadOnlyList<BubbleParticle> Particles => _particles;

    internal void AddForTest(BubbleParticle particle) => _particles.Add(particle);

    /// <summary>
    /// A swim stroke lets out a few bubbles from the head. Call once a frame, before
    /// <see cref="Update"/>, with the swimmer's stroke counter (<see cref="Camera.SwimStrokeCount"/>):
    /// each change of it while <paramref name="eyeInWater"/> spawns one burst of
    /// <see cref="WorldGenerationConfig.SwimStrokeBubbleCount"/> bubbles just in front of
    /// <paramref name="head"/> along <paramref name="facing"/>. A dry eye spawns nothing, and the
    /// counter is still taken in, so a stroke made in the air never bursts late on diving under; the
    /// first call only learns the counter. Returns how many bubbles it spawned.
    /// </summary>
    public int TrackSwimStrokes(int strokeCount, Vector3 head, Vector3 facing, bool eyeInWater, Random? random = null)
    {
        var stroked = _strokesSeen && strokeCount != _lastStrokes;
        _lastStrokes = strokeCount;
        _strokesSeen = true;
        if (!stroked || !eyeInWater)
            return 0;

        var rng = random ?? Random.Shared;
        var forward = facing.NormalizeSafeOr(-Vector3.UnitZ);
        // Out of the mouth: in front of the eye and a touch below it
        var centre = head + forward * WorldGenerationConfig.SwimStrokeBubbleForward - Vector3.UnitY * 0.1f;
        var spread = WorldGenerationConfig.SwimStrokeBubbleSpread;
        for (var i = 0; i < WorldGenerationConfig.SwimStrokeBubbleCount; i++)
        {
            var jitter = new Vector3(
                (rng.NextSingle() * 2f - 1f) * spread,
                (rng.NextSingle() * 2f - 1f) * spread * 0.6f,
                (rng.NextSingle() * 2f - 1f) * spread);
            Add(new BubbleParticle(centre + jitter, rng));
        }
        return WorldGenerationConfig.SwimStrokeBubbleCount;
    }

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
            var f = forward.NormalizeSafeOr(-Vector3.UnitZ);
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
        var jitter = new Vector3(
            (rng.NextSingle() - 0.5f) * 0.12f,
            (rng.NextSingle() - 0.5f) * 0.08f,
            (rng.NextSingle() - 0.5f) * 0.12f);
        Add(new BubbleParticle(mouth + jitter, rng));
    }

    // At the cap the oldest bubble makes room, so the newest are always the ones shown
    private void Add(BubbleParticle bubble)
    {
        if (_particles.Count >= WorldGenerationConfig.BubbleParticleMax)
            _particles.RemoveAt(0);
        _particles.Add(bubble);
    }
}
