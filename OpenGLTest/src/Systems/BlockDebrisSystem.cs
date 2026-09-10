using System;
using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Owns the live mining debris: small textured cubes chipped off on every mining hit plus
/// a bigger burst when the block breaks. Main thread only.
/// </summary>
public class BlockDebrisSystem
{
    private readonly List<BlockDebris> _debris = [];

    public IReadOnlyList<BlockDebris> Debris => _debris;

    /// <summary>Test seam: injects debris without going through the spawn rules.</summary>
    internal void AddForTest(BlockDebris debris) => _debris.Add(debris);

    /// <summary>
    /// Chips <paramref name="count"/> cubes off the block at <paramref name="cell"/>.
    /// Air and unknown ids spawn nothing.
    /// </summary>
    public void Burst(Vector3 cell, byte blockId, int count, Random? random = null)
    {
        if (count <= 0) return;
        if (blockId == BlockType.Air || BlockType.ById[blockId] == null) return;
        var rng = random ?? Random.Shared;
        var center = cell + new Vector3(0.5f);
        for (var i = 0; i < count; i++)
        {
            var jitter = new Vector3(
                (rng.NextSingle() - 0.5f) * 0.6f,
                (rng.NextSingle() - 0.5f) * 0.6f,
                (rng.NextSingle() - 0.5f) * 0.6f);
            var toss = new Vector3(
                (rng.NextSingle() * 2f - 1f) * 1.2f,
                rng.NextSingle() * 1.2f,
                (rng.NextSingle() * 2f - 1f) * 1.2f);
            if (_debris.Count >= WorldGenerationConfig.BlockDebrisMax)
                _debris.RemoveAt(0);
            _debris.Add(new BlockDebris(blockId, center + jitter, toss, random: rng));
        }
    }

    /// <summary>
    /// Advances every cube, removing the dead. Back to front so a removal never skips the next one.
    /// </summary>
    public void Update(float deltaTime, ChunkManager chunks)
    {
        if (deltaTime <= 0f || chunks == null) return;
        for (var i = _debris.Count - 1; i >= 0; i--)
        {
            if (!_debris[i].Update(deltaTime, chunks))
                _debris.RemoveAt(i);
        }
    }
}
