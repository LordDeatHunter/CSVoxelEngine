using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Particles.Heave;

/// <summary>
/// The Skeleton King's slam made visible on the ground (cosmetic: singleplayer and a client, never a server): the
/// surface blocks around where the club struck pop up out of the ground and settle back, in a ring that ripples outward
/// from the strike, highest near it. A copy of each block is drawn raised over the real one, which never changes: no
/// cell, body, save or wire ever sees the heave (<see cref="LiftOf"/>, drawn by <c>ObjectPasses.RenderGroundHeave</c>).
/// Tuning in <see cref="JuiceConfig"/> (<c>SlamHeave*</c>). Main thread only.
/// </summary>
public sealed class GroundHeave
{
    private readonly List<HeavedBlock> _blocks = [];
    private float _clock;

    /// <summary>The blocks of every heave still under way; empty once each has settled.</summary>
    public IReadOnlyList<HeavedBlock> Blocks => _blocks;

    /// <summary>
    /// Heaves the ground round <paramref name="impact"/> (the club's strike, at the feet's level): every column within
    /// <see cref="JuiceConfig.SlamHeaveRadiusBlocks"/> whose ground (found from that level down
    /// <see cref="JuiceConfig.SlamHeaveSearchDepth"/> cells) is a full cube with no full cube over it rises, starting
    /// later the farther it stands (<see cref="JuiceConfig.SlamHeaveWaveSpeed"/>) and rising less. A column of anything
    /// else (a plant, a slab, a wall face, an unloaded cell) stays still.
    /// </summary>
    public void Raise(ChunkManager chunks, Vector3 impact)
    {
        const float radius = JuiceConfig.SlamHeaveRadiusBlocks;
        var reach = (int)MathF.Ceiling(radius);
        var cx = (int)MathF.Floor(impact.X);
        var cz = (int)MathF.Floor(impact.Z);
        var level = (int)MathF.Floor(impact.Y);
        for (var x = cx - reach; x <= cx + reach; x++)
        for (var z = cz - reach; z <= cz + reach; z++)
        {
            var dx = x + 0.5f - impact.X;
            var dz = z + 0.5f - impact.Z;
            var distance = MathF.Sqrt(dx * dx + dz * dz);
            if (distance > radius || !TryFindGround(chunks, x, z, level, out var y, out var id)) continue;

            var nearness = 1f - distance / radius;
            var center = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
            _blocks.Add(new HeavedBlock(center, id, BlockDebrisSystem.TintAt(center, id, chunks.ColumnSource),
                _clock + distance / JuiceConfig.SlamHeaveWaveSpeed,
                JuiceConfig.SlamHeaveHeight * (JuiceConfig.SlamHeaveEdgeShare + (1f - JuiceConfig.SlamHeaveEdgeShare) * nearness)));
        }
    }

    /// <summary>How far <paramref name="block"/> stands raised now: up and back down over
    /// <see cref="JuiceConfig.SlamHeaveSeconds"/> from its start, 0 before and after.</summary>
    public float LiftOf(in HeavedBlock block)
    {
        var phase = (_clock - block.Start) / JuiceConfig.SlamHeaveSeconds;
        return phase is <= 0f or >= 1f ? 0f : block.Height * MathF.Sin(MathF.PI * phase);
    }

    /// <summary>Advances every heave and drops the blocks that have settled.</summary>
    public void Update(float deltaTime)
    {
        if (_blocks.Count == 0)
        {
            _clock = 0f;
            return;
        }
        _clock += deltaTime;
        _blocks.RemoveAll(b => _clock >= b.Start + JuiceConfig.SlamHeaveSeconds);
    }

    // The ground of column (x, z) at or a few cells under level: a full cube under no full cube (a wall's face is no ground)
    private static bool TryFindGround(ChunkManager chunks, int x, int z, int level, out int y, out ushort id)
    {
        id = 0;
        for (y = level; y >= level - JuiceConfig.SlamHeaveSearchDepth; y--)
        {
            if (!chunks.TryGetCell(x, y, z, out id, out var state)) return false;
            if (!BlockType.HoldsFullFace(id, state)) continue;
            return !(chunks.TryGetCell(x, y + 1, z, out var above, out var aboveState) && BlockType.HoldsFullFace(above, aboveState));
        }
        return false;
    }
}
