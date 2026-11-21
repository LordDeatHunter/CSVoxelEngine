using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Effects.Sky;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Particles;

/// <summary>
/// Owns the live falling leaves: ambient fall sampled off canopy around the camera plus
/// bursts where the canopy was disturbed (mined, felled, blasted). Every leaf wears the look of
/// the leaves it fell from (<see cref="LeafLook"/>): a crystal crown sheds crystal, an oak its
/// biome's green. Main thread only.
/// </summary>
public class LeafParticleSystem
{
    private readonly ParticleRing<LeafParticle> _particles = new(ParticleConfig.LeafParticleMax);
    private float _spawnDebt;

    public IReadOnlyList<LeafParticle> Particles => _particles;

    /// <summary>Test seam: injects particles without going through the spawn rules.</summary>
    internal void AddForTest(LeafParticle particle) => _particles.Add(particle);

    /// <summary>
    /// Shakes <paramref name="count"/> leaves loose around <paramref name="cell"/>: one burst
    /// per disturbed canopy cell (a mined leaf or log). <paramref name="blockId"/> is the block
    /// that was there, whose leaves the burst wears (<see cref="LeafLook.TryFor"/>; a block that
    /// sheds none bursts nothing), tinted from <paramref name="columns"/> (null means worldgen).
    /// </summary>
    public void Burst(Vector3 cell, ushort blockId, int count, Random? random = null, IColumnSource? columns = null)
    {
        if (count <= 0 || !LeafLook.TryFor(blockId, cell, columns, out var look)) return;
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
            Spawn(cell + new Vector3(0.5f) + jitter, toss, rng, look);
        }
    }

    /// <summary>
    /// Shakes the canopy in <paramref name="cells"/> (each with the leaf block it held) loose,
    /// sampling at most <paramref name="count"/> of them: a felled tree or a blast disturbs far
    /// more leaves than should ever become particles at once.
    /// </summary>
    public void BurstCells(IReadOnlyList<(Vector3 Cell, ushort Id)> cells, int count, Random? random = null,
        IColumnSource? columns = null)
    {
        if (cells.Count == 0 || count <= 0) return;
        var rng = random ?? Random.Shared;
        if (cells.Count <= count)
        {
            foreach (var (cell, id) in cells)
                Burst(cell, id, 1, rng, columns);
            return;
        }

        var step = (float)cells.Count / count;
        for (var i = 0; i < count; i++)
        {
            var (cell, id) = cells[(int)(i * step)];
            Burst(cell, id, 1, rng, columns);
        }
    }

    /// <summary>
    /// Samples ambient fall off nearby canopy and advances every leaf, removing the dead.
    /// Back to front so a removal never skips the next leaf. <paramref name="windTime"/> is the
    /// lighting clock the shared <see cref="Wind"/> reads: the falling leaves drift down its heading,
    /// pushed by the same gusts (<see cref="Wind.Gust"/>) the canopy sways in. Null is still air.
    /// </summary>
    public void Update(float deltaTime, Vector3 cameraPos, ChunkManager chunks, Random? random = null,
        double? windTime = null)
    {
        if (deltaTime <= 0f || chunks == null) return;
        var rng = random ?? Random.Shared;

        SpawnAmbient(deltaTime, cameraPos, chunks, rng);

        // One heading and gust offset a frame; only the gust phase is per leaf
        var wind = Vector2.Zero;
        var gustOffset = Vector2.Zero;
        var t = windTime ?? 0.0;
        if (windTime.HasValue)
        {
            wind = Wind.Direction(t);
            gustOffset = Wind.Offset(t, SkyConfig.WindGustSpeed);
        }

        for (var i = _particles.Count - 1; i >= 0; i--)
        {
            var leaf = _particles[i];
            var gust = windTime.HasValue
                ? Wind.Gust(new Vector2(leaf.Position.X, leaf.Position.Z), t, gustOffset)
                : 1f;
            if (!leaf.Update(deltaTime, chunks, wind, gust))
                _particles.RemoveAt(i);
        }
    }

    private void SpawnAmbient(float deltaTime, Vector3 cameraPos, ChunkManager chunks, Random rng)
    {
        _spawnDebt += deltaTime * ParticleConfig.LeafParticleSpawnPerSecond;
        var radius = ParticleConfig.LeafParticleSpawnRadius;
        var tries = ParticleConfig.LeafParticleSpawnTries;
        var top = (int)MathF.Floor(cameraPos.Y + radius * 0.5f);
        const int scanDepth = 32;

        while (_spawnDebt >= 1f)
        {
            _spawnDebt -= 1f;
            if (_particles.Count >= ParticleConfig.LeafParticleMax)
            {
                _spawnDebt = MathF.Min(_spawnDebt, 1f);
                continue;
            }

            var spawned = false;
            for (var t = 0; t < tries && !spawned; t++)
            {
                var x = (int)MathF.Floor(cameraPos.X + (rng.NextSingle() * 2f - 1f) * radius);
                var z = (int)MathF.Floor(cameraPos.Z + (rng.NextSingle() * 2f - 1f) * radius);

                // Scan the column top-down for the first leaf of any species with room to fall
                // into: the dripping bottom shell of a canopy, never its buried interior.
                for (var y = top; y > top - scanDepth; y--)
                {
                    var leafId = chunks.GetBlockGlobal(x, y, z);
                    if (!TreeGenerator.IsLeaf(leafId)) continue;
                    var below = chunks.GetBlockGlobal(x, y - 1, z);
                    if (TreeGenerator.IsLeaf(below) || BlockType.IsSolid(below)) continue;
                    if (!LeafLook.TryFor(leafId, new Vector3(x, y, z), chunks.ColumnSource, out var look)) break;

                    Spawn(new Vector3(
                        x + 0.3f + rng.NextSingle() * 0.4f,
                        y + rng.NextSingle() * 0.5f,
                        z + 0.3f + rng.NextSingle() * 0.4f), null, rng, look);
                    spawned = true;
                    break;
                }
            }
        }
    }

    private void Spawn(Vector3 position, Vector3? toss, Random rng, LeafLook look)
    {
        if (_particles.Count >= ParticleConfig.LeafParticleMax)
            _particles.RemoveAt(0);
        _particles.Add(new LeafParticle(position, toss, random: rng, look: look));
    }
}
