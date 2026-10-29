using System;
using System.Numerics;

namespace VoxelEngine.WorldGen;

/// <summary>
/// The relief regime layer of the land height, orthogonal to the climate: a continent-scale
/// highland weight (<see cref="HighlandWeight"/>) that lifts the base height
/// (<see cref="HighlandLift"/>) and steepens the hills (<see cref="HillGain"/>), and the linear
/// mountain ranges (<see cref="MountainHeight"/>) that rise only along highlands. Every term is
/// exactly neutral where the highland weight is 0 (a lift of 0, a hill gain of 1, no range), so
/// unmasked land comes out bit-identical to the land height without this layer, and the weight
/// fades out around the spawn (<see cref="SpawnClear"/>). A pure function of (x, z) and the seed,
/// safe from any worker thread.
/// </summary>
internal static class TerrainRelief
{
    // Salts the per-cell range hash so it never correlates with another SplitMix64.Hash user
    private const int RangeSalt = 0x52414E47;

    /// <summary>0 within <see cref="WorldGenerationConfig.ReliefSpawnClearStart"/> of the origin, 1 beyond
    /// <see cref="WorldGenerationConfig.ReliefSpawnClearEnd"/>, smooth between.</summary>
    internal static float SpawnClear(float x, float z) =>
        MathUtils.SmoothStep(WorldGenerationConfig.ReliefSpawnClearStart, WorldGenerationConfig.ReliefSpawnClearEnd,
            MathF.Sqrt(x * x + z * z));

    /// <summary>
    /// 0..1 how highland (x, z) is: a smoothstep of the low-frequency <see cref="TerrainNoise.ReliefNoise"/>,
    /// faded out around the spawn. Exactly 0 over lowlands.
    /// </summary>
    internal static float HighlandWeight(float x, float z)
    {
        // HighlandsEnabled is a compile-time const, so an early return on it would read as
        // unreachable (see BiomeWeights); a multiplier keeps the toggle a real expression.
        var enabled = WorldGenerationConfig.HighlandsEnabled ? 1f : 0f;
        var clear = SpawnClear(x, z);
        if (clear <= 0f) return 0f;

        return MathUtils.SmoothStep(WorldGenerationConfig.HighlandMaskStart, WorldGenerationConfig.HighlandMaskEnd,
            TerrainNoise.ReliefNoise.GetNoise(x, z)) * clear * enabled;
    }

    /// <summary>Blocks a highland raises the base height; exactly 0 at a highland weight of 0.</summary>
    internal static float HighlandLift(float highland) => WorldGenerationConfig.HighlandBaseLift * highland;

    /// <summary>The factor a highland multiplies the hill amplitude by; exactly 1 at a highland weight of 0.</summary>
    internal static float HillGain(float highland) => 1f + WorldGenerationConfig.HighlandHillGain * highland;

    /// <summary>
    /// Blocks a mountain range raises the forest floor at (x, z): the linear range shape
    /// (<see cref="RangeShape"/>) over winding coordinates, gated by a rarity mask times the
    /// <paramref name="highland"/> weight, so a range only ever rises inside a highland. Exactly 0
    /// where the highland weight is 0, before any mountain noise is read.
    /// </summary>
    internal static float MountainHeight(float x, float z, float highland)
    {
        if (highland <= 0f) return 0f;

        // MountainsEnabled is a compile-time const, so an early return on it would read as
        // unreachable (see BiomeWeights); a multiplier keeps the toggle a real expression.
        var enabled = WorldGenerationConfig.MountainsEnabled ? 1f : 0f;
        var mask = MathUtils.SmoothStep(WorldGenerationConfig.MountainMaskStart,
            WorldGenerationConfig.MountainMaskEnd, TerrainNoise.MountainMask.GetNoise(x, z)) * highland * enabled;
        if (mask <= 0f) return 0f;

        var warpedX = x;
        var warpedZ = z;
        TerrainNoise.MountainWarp.DomainWarp(ref warpedX, ref warpedZ);
        return RangeShape(warpedX, warpedZ) * mask * WorldGenerationConfig.MountainAmplitude;
    }

    /// <summary>The weight of the second range cell along one axis at a cell fraction: 0 in the
    /// first cell's core, 1 in the second's, a smooth crossfade in the narrow band between.</summary>
    internal static float RangeBlend(float frac) =>
        MathUtils.SmoothStep(0.5f - WorldGenerationConfig.MountainRangeBlend,
            0.5f + WorldGenerationConfig.MountainRangeBlend, frac);

    /// <summary>
    /// The range-cell lattice coordinates (u, v) of already-warped (x, z), in cells and offset by
    /// half a cell: the point jittered by <see cref="TerrainNoise.MountainRangeCellWarp"/> and
    /// divided by <see cref="WorldGenerationConfig.MountainRangeCellBlocks"/>. Only the cell lookup
    /// is jittered, so the cell borders wander instead of running straight north-south and
    /// east-west; <see cref="CellShape"/> still reads the unjittered point. The jitter is a
    /// continuous function of position, so the field stays continuous.
    /// </summary>
    internal static Vector2 RangeLattice(float x, float z)
    {
        var jitteredX = x;
        var jitteredZ = z;
        TerrainNoise.MountainRangeCellWarp.DomainWarp(ref jitteredX, ref jitteredZ);
        return new Vector2(jitteredX / WorldGenerationConfig.MountainRangeCellBlocks - 0.5f,
            jitteredZ / WorldGenerationConfig.MountainRangeCellBlocks - 0.5f);
    }

    /// <summary>
    /// The 0..1 range shape at already-warped (x, z): each <see cref="WorldGenerationConfig.MountainRangeCellBlocks"/>
    /// cell runs its ranges along its own hashed direction (<see cref="CellShape"/>), and the shapes
    /// of neighboring cells are crossfaded only in a narrow band at the cell borders
    /// (<see cref="RangeShapeAt"/> over <see cref="RangeLattice"/>).
    /// </summary>
    internal static float RangeShape(float x, float z) => RangeShapeAt(RangeLattice(x, z), x, z);

    /// <summary>
    /// The range shape at (x, z) for its lattice coordinates <paramref name="lattice"/>. The lattice
    /// is offset by half a cell, so the cell index switches at a cell center, where all the weight
    /// already sits on one cell: the field is continuous by construction and most points evaluate
    /// one cell.
    /// </summary>
    internal static float RangeShapeAt(Vector2 lattice, float x, float z)
    {
        var u = lattice.X;
        var v = lattice.Y;
        var i0 = MathUtils.FloorToInt(u);
        var j0 = MathUtils.FloorToInt(v);
        var bx = RangeBlend(u - i0);
        var bz = RangeBlend(v - j0);

        var sum = 0f;
        for (var di = 0; di < 2; di++)
        {
            var wx = di == 0 ? 1f - bx : bx;
            if (wx <= 0f) continue;
            for (var dj = 0; dj < 2; dj++)
            {
                var w = wx * (dj == 0 ? 1f - bz : bz);
                if (w <= 0f) continue;
                sum += w * CellShape(i0 + di, j0 + dj, x, z);
            }
        }

        return sum;
    }

    /// <summary>The unit horizontal direction the ranges of range cell (cellX, cellZ) run along.</summary>
    internal static Vector2 RangeDirection(int cellX, int cellZ) => DirectionOf(CellHash(cellX, cellZ));

    /// <summary>
    /// One range cell's 0..1 shape at already-warped (x, z): the ridged mountain field and the
    /// lower-frequency spine, both sampled stretched along the cell's direction by
    /// <see cref="WorldGenerationConfig.MountainRangeStretch"/> (so ridges run as chains along it)
    /// and offset per cell (so neighboring cells decorrelate). The ridges ride the spine:
    /// spine^p * (floor + (1 - floor) * ridge^sharpness).
    /// </summary>
    internal static float CellShape(int ci, int cj, float x, float z)
    {
        var h = CellHash(ci, cj);
        var dir = DirectionOf(h);
        var offsetX = SplitMix64.Unit(h, 1) * 4096f;
        var offsetZ = SplitMix64.Unit(h, 2) * 4096f;

        var along = dir.X * x + dir.Y * z;
        var across = -dir.Y * x + dir.X * z;
        var sx = along / WorldGenerationConfig.MountainRangeStretch + offsetX;
        var sz = across + offsetZ;

        var ridge = MathF.Pow(1f - MathF.Abs(TerrainNoise.MountainNoise.GetNoise(sx, sz)),
            WorldGenerationConfig.MountainSharpness);
        var spine = MathF.Pow(1f - MathF.Abs(TerrainNoise.MountainSpineNoise.GetNoise(sx, sz)),
            WorldGenerationConfig.MountainSpineSharpness);
        return spine * (WorldGenerationConfig.MountainSpineFloor
            + (1f - WorldGenerationConfig.MountainSpineFloor) * ridge);
    }

    private static Vector2 DirectionOf(ulong cellHash)
    {
        var angle = SplitMix64.Unit(cellHash, 0) * MathF.Tau;
        return new Vector2(MathF.Cos(angle), MathF.Sin(angle));
    }

    private static ulong CellHash(int cx, int cz) => SplitMix64.Hash(cx, cz, WorldGenerationConfig.Seed ^ RangeSalt);
}
