using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Particles;

/// <summary>One water cell column a ripple may spread over: its cell and the world Y of its surface.</summary>
public readonly record struct RippleCell(int X, int Z, float Top);

/// <summary>
/// Where a <see cref="WaterRipple"/> may spread, GL-free: the water cells at the ripple's own level
/// within its reach. A ring never leaves the water it was born on, so a single block of water gets a
/// ring that stops at its edge, and each cell carries its own surface height so the ring follows a
/// stepped surface. Gathered once at the crossing (a second of ring life does not outlast the water).
/// </summary>
public static class RippleSurface
{
    /// <summary>The cells within <paramref name="reach"/> blocks of <paramref name="center"/> whose water surface
    /// lies within <see cref="ParticleConfig.RippleLevelTolerance"/> of <paramref name="top"/> (the surface at the
    /// center). The center's own cell is always first; lava and dry cells are left out.</summary>
    public static List<RippleCell> Gather(ChunkManager chunks, Vector3 center, float top, float reach)
    {
        var cells = new List<RippleCell>();
        var y = MathUtils.FloorToInt(top - 0.001f);
        var cx = MathUtils.FloorToInt(center.X);
        var cz = MathUtils.FloorToInt(center.Z);
        cells.Add(new RippleCell(cx, cz, top));

        var span = (int)MathF.Ceiling(reach);
        for (var x = cx - span; x <= cx + span; x++)
        for (var z = cz - span; z <= cz + span; z++)
        {
            if (x == cx && z == cz) continue;
            if (!ReachesCell(center, x, z, reach)) continue;
            if (!TryWaterTop(chunks, x, y, z, out var cellTop)) continue;
            if (MathF.Abs(cellTop - top) > ParticleConfig.RippleLevelTolerance) continue;
            cells.Add(new RippleCell(x, z, cellTop));
        }

        return cells;
    }

    /// <summary>True when any point of the cell (<paramref name="x"/>, <paramref name="z"/>) lies within
    /// <paramref name="reach"/> of <paramref name="center"/> in the horizontal plane.</summary>
    public static bool ReachesCell(Vector3 center, int x, int z, float reach)
    {
        var dx = MathF.Max(MathF.Max(x - center.X, center.X - (x + 1)), 0f);
        var dz = MathF.Max(MathF.Max(z - center.Z, center.Z - (z + 1)), 0f);
        return dx * dx + dz * dz <= reach * reach;
    }

    private static bool TryWaterTop(ChunkManager chunks, int x, int y, int z, out float top)
    {
        top = 0f;
        if (!chunks.TryGetCell(x, y, z, out var id, out _) || !BlockType.IsFluid(id)) return false;
        if (BlockType.Emission[id] != 0) return false;
        top = chunks.GetFluidColumnTopGlobal(x, y, z);
        return float.IsFinite(top);
    }
}
