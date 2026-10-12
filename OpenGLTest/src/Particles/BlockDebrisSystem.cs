using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Effects.Climate;

namespace VoxelEngine.Particles;

/// <summary>
/// Owns the live mining debris: small textured cubes chipped off on every mining hit plus
/// a bigger burst when the block breaks, and the ground dust (<see cref="GroundDust"/>) kicked up
/// by landings and footfalls, which shares the chips' pool and draw. Main thread only.
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
        var tint = TintAt(cell, blockId, columns);
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
    /// Kicks <paramref name="count"/> chips of the block at <paramref name="cell"/> out of
    /// <paramref name="origin"/> (a landing body's feet, a footfall): each flies off on its own
    /// heading at about <paramref name="speed"/> blocks per second, mostly sideways with a little
    /// lift, so the puff spreads low along the ground. The launch is exactly that, with none of
    /// <see cref="Burst"/>'s pop, so the caller's speed curve owns it. The chips wear the block's
    /// surface (<see cref="DebrisLook.Surface"/>) and are tinted like <see cref="Burst"/>'s; air and
    /// unknown ids spawn nothing. <paramref name="drift"/> is added to every chip's launch (a
    /// footfall kicks its grit up and back).
    /// </summary>
    public void Puff(Vector3 origin, Vector3 cell, byte blockId, int count, float speed, float size,
        Random? random = null, IColumnSource? columns = null, Vector3 drift = default)
    {
        if (blockId == BlockType.Air || BlockType.ById[blockId] == null) return;
        Spray(origin, blockId, DebrisLook.Surface, TintAt(cell, blockId, columns), count, speed, size,
            radius: 0.15f, lift: 0.35f, random ?? Random.Shared, drift);
    }

    /// <summary>
    /// A whole block settling into <paramref name="cell"/> (a falling sand cube) throws
    /// <paramref name="count"/> chips of itself out from under its rim: they start on a ring just
    /// outside the cell's sides at its floor, never inside the solid block, and fly off outward like a
    /// <see cref="Puff"/>, so the landing reads as a skirt of dust. Air and unknown ids spawn nothing.
    /// </summary>
    public void Skirt(Vector3 cell, byte blockId, int count, float speed, float size, Random? random = null,
        IColumnSource? columns = null)
    {
        if (blockId == BlockType.Air || BlockType.ById[blockId] == null) return;
        var origin = cell + new Vector3(0.5f, size, 0.5f);
        Spray(origin, blockId, DebrisLook.Surface, TintAt(cell, blockId, columns), count, speed, size,
            radius: 0.15f, lift: 0.35f, random ?? Random.Shared, innerRadius: 0.5f + size);
    }

    /// <summary>
    /// Throws <paramref name="count"/> chips of one flat sRGB <paramref name="colorSrgb"/> (a
    /// slime's goo, <see cref="DebrisLook.Color"/>) out of a body whose feet are at
    /// <paramref name="origin"/> and which is <paramref name="radius"/> across: they burst from under
    /// its rim at about <paramref name="speed"/> blocks per second, higher than dust, so the landing
    /// reads as a splat.
    /// </summary>
    public void Splat(Vector3 origin, Vector3 colorSrgb, float radius, int count, float speed, float size,
        Random? random = null) =>
        Spray(origin, BlockType.Air, DebrisLook.Color, BiomeTint.VertexTint(colorSrgb), count, speed, size,
            radius, lift: 0.6f, random ?? Random.Shared);

    /// <summary>
    /// Throws <paramref name="count"/> droplets of one flat sRGB <paramref name="colorSrgb"/> (pale water,
    /// <see cref="DebrisLook.Color"/>) up off a water surface at <paramref name="origin"/>, from a ring
    /// <paramref name="radius"/> across: steeper than a splat, so a body crossing the surface reads as a
    /// splash (<see cref="WaterSplash"/>).
    /// </summary>
    public void Splash(Vector3 origin, Vector3 colorSrgb, float radius, int count, float speed, float size,
        Random? random = null) =>
        Spray(origin, BlockType.Air, DebrisLook.Color, BiomeTint.VertexTint(colorSrgb), count, speed, size,
            radius, lift: 1.3f, random ?? Random.Shared);

    /// <summary>
    /// Throws <paramref name="count"/> pale chips of one flat <paramref name="colorSrgb"/> (a
    /// connecting hit's spark, <see cref="DebrisLook.Color"/>) out of a body at
    /// <paramref name="origin"/> that is <paramref name="radius"/> across: they burst from around
    /// it at about <paramref name="speed"/> blocks per second, mostly sideways.
    /// </summary>
    public void Spark(Vector3 origin, Vector3 colorSrgb, float radius, int count, float speed, float size,
        Random? random = null) =>
        Spray(origin, BlockType.Air, DebrisLook.Color, BiomeTint.VertexTint(colorSrgb), count, speed, size,
            radius, lift: 0.8f, random ?? Random.Shared);

    // The ring launch Puff, Skirt and Splat share: headings spread round the ring with a jitter (so
    // even a three-chip puff fans out), each starting innerRadius plus up to radius out along its
    // heading, flying at 60-100 % of speed sideways and lift..2 lift of it upward, plus a shared
    // drift, launched exactly (no mining pop). A square block's rim is further out toward its corners,
    // so a Skirt pushes each start out to the cell's square edge along its heading.
    private void Spray(Vector3 origin, byte blockId, DebrisLook look, Vector3 tint, int count, float speed,
        float size, float radius, float lift, Random rng, Vector3 drift = default, float innerRadius = 0f)
    {
        if (count <= 0 || speed <= 0f) return;
        for (var i = 0; i < count; i++)
        {
            var angle = (i + rng.NextSingle()) * MathF.Tau / count;
            var heading = new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle));
            var launch = heading * (speed * (0.6f + 0.4f * rng.NextSingle()))
                         + new Vector3(0f, speed * lift * (1f + rng.NextSingle()), 0f) + drift;
            var inner = innerRadius > 0f
                ? innerRadius / MathF.Max(MathF.Abs(heading.X), MathF.Abs(heading.Z))
                : 0f;
            var start = origin + heading * (inner + radius * rng.NextSingle());
            if (_debris.Count >= WorldGenerationConfig.BlockDebrisMax)
                _debris.RemoveAt(0);
            _debris.Add(new BlockDebris(blockId, start, launch, size * (0.75f + 0.5f * rng.NextSingle()), rng,
                tint, pop: false, look: look));
        }
    }

    /// <summary>
    /// The plain path's <c>uBiomeTint</c> for chips of <paramref name="blockId"/> from
    /// <paramref name="cell"/>: a biome-tinted block wears the field there, read from
    /// <paramref name="columns"/> (null means worldgen), and anything else, or a column that source
    /// does not have yet (a client's streamed column that has not arrived), is untinted white.
    /// </summary>
    private static Vector3 TintAt(Vector3 cell, byte blockId, IColumnSource? columns) =>
        BlockType.ById[blockId]?.BiomeTint is { } bt
        && BiomeTint.TryCellTint(columns ?? WorldgenColumnSource.Instance, bt.Palette,
            (int)MathF.Floor(cell.X), (int)MathF.Floor(cell.Z), out var cellTint)
            ? cellTint
            : Vector3.One;

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
