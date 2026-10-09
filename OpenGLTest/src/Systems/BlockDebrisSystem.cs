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
    private readonly ParticleRing<BlockDebris> _debris = new(WorldGenerationConfig.BlockDebrisMax);

    public IReadOnlyList<BlockDebris> Debris => _debris;

    /// <summary>Test seam: injects debris without going through the spawn rules.</summary>
    internal void AddForTest(BlockDebris debris) => _debris.Add(debris);

    /// <summary>
    /// Chips <paramref name="count"/> cubes off the block at <paramref name="cell"/>.
    /// Air and unknown ids spawn nothing. A biome-tinted block's chips wear the tint field at the
    /// cell, read from <paramref name="columns"/> (the world's <see cref="ChunkManager.ColumnSource"/>;
    /// null means worldgen); a column that source does not have yet (a client's streamed column
    /// that has not arrived) leaves them untinted.
    /// </summary>
    public void Burst(Vector3 cell, byte blockId, int count, Random? random = null, IColumnSource? columns = null)
    {
        if (count <= 0) return;
        if (blockId == BlockType.Air || BlockType.ById[blockId] == null) return;
        var rng = random ?? Random.Shared;
        var center = cell + new Vector3(0.5f);
        // One tint per burst: every chip of a biome-tinted block wears the field at its cell
        var tint = BlockType.ById[blockId]!.BiomeTint is { } bt
            && BiomeTint.TryCellTint(columns ?? WorldgenColumnSource.Instance, bt.Palette,
                (int)MathF.Floor(cell.X), (int)MathF.Floor(cell.Z), out var cellTint)
            ? cellTint
            : Vector3.One;
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
            _debris.Add(new BlockDebris(blockId, center + jitter, toss, random: rng, tint: tint));
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
