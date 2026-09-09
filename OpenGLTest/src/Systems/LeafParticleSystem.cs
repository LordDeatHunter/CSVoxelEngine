using System;
using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Owns the live falling leaves: ambient fall sampled off canopy around the camera plus
/// bursts where the canopy was disturbed (mined, felled, blasted). Main thread only.
/// </summary>
public class LeafParticleSystem
{
    private readonly List<LeafParticle> _particles = [];
    private float _spawnDebt;
    private byte _leavesId;
    private bool _leavesResolved;

    public IReadOnlyList<LeafParticle> Particles => _particles;

    /// <summary>Test seam: injects particles without going through the spawn rules.</summary>
    internal void AddForTest(LeafParticle particle) => _particles.Add(particle);

    /// <summary>
    /// Shakes <paramref name="count"/> leaves loose around <paramref name="cell"/>: one burst
    /// per disturbed canopy cell (a mined leaf or log).
    /// </summary>
    public void Burst(Vector3 cell, int count, Random? random = null)
    {
        var rng = random ?? Random.Shared;
        for (var i = 0; i < count; i++)
        {
            var jitter = new Vector3(
                rng.NextSingle() - 0.5f,
                rng.NextSingle() - 0.5f,
                rng.NextSingle() - 0.5f);
            var toss = new Vector3(
                (rng.NextSingle() * 2f - 1f) * 1.5f,
                rng.NextSingle() * 1f,
                (rng.NextSingle() * 2f - 1f) * 1.5f);
            Spawn(cell + new Vector3(0.5f) + jitter, toss, rng);
        }
    }

    /// <summary>
    /// Shakes the canopy in <paramref name="cells"/> loose, sampling at most
    /// <paramref name="count"/> of them: a felled tree or a blast disturbs far more leaves
    /// than should ever become particles at once.
    /// </summary>
    public void BurstCells(IReadOnlyList<Vector3> cells, int count, Random? random = null)
    {
        if (cells.Count == 0 || count <= 0) return;
        var rng = random ?? Random.Shared;
        if (cells.Count <= count)
        {
            foreach (var cell in cells)
                Burst(cell, 1, rng);
            return;
        }

        var step = (float)cells.Count / count;
        for (var i = 0; i < count; i++)
            Burst(cells[(int)(i * step)], 1, rng);
    }

    /// <summary>
    /// Samples ambient fall off nearby canopy and advances every leaf, removing the dead.
    /// Back to front so a removal never skips the next leaf.
    /// </summary>
    public void Update(float deltaTime, Vector3 cameraPos, ChunkManager chunks, Random? random = null)
    {
        if (deltaTime <= 0f || chunks == null) return;
        var rng = random ?? Random.Shared;

        SpawnAmbient(deltaTime, cameraPos, chunks, rng);

        for (var i = _particles.Count - 1; i >= 0; i--)
        {
            if (!_particles[i].Update(deltaTime, chunks))
                _particles.RemoveAt(i);
        }
    }

    private void SpawnAmbient(float deltaTime, Vector3 cameraPos, ChunkManager chunks, Random rng)
    {
        if (!TryLeavesId(out var leavesId)) return;

        _spawnDebt += deltaTime * WorldGenerationConfig.LeafParticleSpawnPerSecond;
        var radius = WorldGenerationConfig.LeafParticleSpawnRadius;
        var tries = WorldGenerationConfig.LeafParticleSpawnTries;
        var top = (int)MathF.Floor(cameraPos.Y + radius * 0.5f);
        const int scanDepth = 32;

        while (_spawnDebt >= 1f)
        {
            _spawnDebt -= 1f;
            if (_particles.Count >= WorldGenerationConfig.LeafParticleMax)
            {
                _spawnDebt = MathF.Min(_spawnDebt, 1f);
                continue;
            }

            var spawned = false;
            for (var t = 0; t < tries && !spawned; t++)
            {
                var x = (int)MathF.Floor(cameraPos.X + (rng.NextSingle() * 2f - 1f) * radius);
                var z = (int)MathF.Floor(cameraPos.Z + (rng.NextSingle() * 2f - 1f) * radius);

                // Scan the column top-down for the first leaf with room to fall into: the
                // dripping bottom shell of a canopy, never its buried interior.
                for (var y = top; y > top - scanDepth; y--)
                {
                    if (chunks.GetBlockGlobal(x, y, z) != leavesId) continue;
                    var below = chunks.GetBlockGlobal(x, y - 1, z);
                    if (below == leavesId || BlockType.IsSolid(below)) continue;

                    Spawn(new Vector3(
                        x + 0.3f + rng.NextSingle() * 0.4f,
                        y + rng.NextSingle() * 0.5f,
                        z + 0.3f + rng.NextSingle() * 0.4f), null, rng);
                    spawned = true;
                    break;
                }
            }
        }
    }

    private void Spawn(Vector3 position, Vector3? toss, Random rng)
    {
        if (_particles.Count >= WorldGenerationConfig.LeafParticleMax)
            _particles.RemoveAt(0);
        _particles.Add(new LeafParticle(position, toss, random: rng));
    }

    private bool TryLeavesId(out byte leavesId)
    {
        if (!_leavesResolved)
        {
            try
            {
                _leavesId = ContentRegistry.RequireBlock("leaves").Id;
            }
            catch
            {
                leavesId = 0;
                return false;
            }
            _leavesResolved = true;
        }
        leavesId = _leavesId;
        return true;
    }
}
