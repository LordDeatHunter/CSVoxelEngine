using System;
using System.Runtime.CompilerServices;
using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.WorldGen.Features;
using VoxelEngine.WorldGen.Features.Underground;

namespace VoxelEngine.WorldGen.Hell;

/// <summary>
/// Hell (plans/LAYERS.md): every cell at or below <see cref="WorldGenerationConfig.HellTopY"/> belongs
/// to this generator alone. Hell is a stack of layers <see cref="WorldGenerationConfig.HellLayerPeriodBlocks"/>
/// tall repeating downward forever (<see cref="LayerIndex"/> counts them from 0 under the barrier): the
/// top <see cref="PocketHeight"/> rows of a layer are its cavern pocket, the bottom
/// <see cref="WorldGenerationConfig.HellSlabThicknessBlocks"/> rows an unbroken hellrock slab. A pocket
/// cell is open where a 3D cavern noise, sampled on a world-aligned lattice every
/// <see cref="WorldGenerationConfig.HellNoiseStride"/> blocks and blended trilinearly at the cell's
/// center (the way <see cref="NoiseCaves"/> samples its fields), exceeds the row's threshold
/// (<see cref="RowThreshold"/>): the layer's openness, which grows with the layer index, plus an edge
/// bias that closes the cells near the pocket's ceiling and floor, so both follow the noise rather
/// than a plane. An open cell fewer than <see cref="LavaDepth"/> cells above its pocket's floor holds a
/// lava source, so the floor's dips pond lava; the level is flat across a layer, so every lava cell's
/// sides and bottom are lava or hellrock and the pools never flow. A row whose threshold no blend can
/// pass (<see cref="RowSolidLimit"/>) is hellrock by definition and never samples the noise.
/// A pure function of world position and seed: the point query <see cref="BlockAt"/> and
/// <see cref="Stamp"/> evaluate the same rows, lattice points and fraction expressions, so they agree
/// bit for bit across every chunk seam, and a chunk reads nothing outside itself.
/// </summary>
public static class HellGenerator
{
    private const int Stride = WorldGenerationConfig.HellNoiseStride;
    private const int Mask = Stride - 1;
    private const int Cells = Chunk.Size / Stride;   // lattice cells per chunk axis
    private const int Grid = Cells + 1;              // lattice points per chunk axis, both faces included
    private const int GridSq = Grid * Grid;
    private const int GridCount = GridSq * Grid;
    private const float InvStride = 1f / Stride;

    private const int Period = WorldGenerationConfig.HellLayerPeriodBlocks;

    /// <summary>Rows of open cavern at the top of every layer, above its slab.</summary>
    public const int PocketHeight = Period - WorldGenerationConfig.HellSlabThicknessBlocks;

    /// <summary>A row whose threshold is at or above this is hellrock by definition: the two-octave
    /// cavern noise is normalized to about -1..1, so no blend passes it anyway, and the rule lets
    /// such rows (the slab, the pocket's outermost rows) skip the noise.</summary>
    public const float RowSolidLimit = 1f;

    private static FastNoiseLite CavernNoise;

    /// <summary>The hellrock block; resolved once in the static constructor.</summary>
    public static readonly byte HellrockBlock;

    /// <summary>The lava block the pools are made of.</summary>
    public static readonly byte LavaBlock;

    static HellGenerator()
    {
        Reseed();
        HellrockBlock = ContentRegistry.RequireBlock("hellrock").Id;
        LavaBlock = ContentRegistry.RequireBlock("lava").Id;
    }

    /// <summary>Builds the noise from the current seed (static constructor and <see cref="WorldSeed.Apply"/>).</summary>
    internal static void Reseed() =>
        CavernNoise = TerrainGenerator.MakeNoise(163, WorldGenerationConfig.HellCavernFrequency, octaves: 2);

    /// <summary>Whether world y lies in hell.</summary>
    public static bool InHell(int y) => y <= WorldGenerationConfig.HellTopY;

    /// <summary>Whether the chunk whose floor is world y <paramref name="worldY0"/> lies wholly in hell.</summary>
    public static bool Covers(int worldY0) => InHell(worldY0 + Chunk.Size - 1);

    /// <summary>The hell layer world y lies in: 0 right under the barrier, one more every
    /// <see cref="WorldGenerationConfig.HellLayerPeriodBlocks"/> down, -1 above hell.</summary>
    public static int LayerIndex(int y) => InHell(y) ? (WorldGenerationConfig.HellTopY - y) / Period : -1;

    /// <summary>How many rows below its layer's top a hell row lies: 0..<see cref="PocketHeight"/> - 1
    /// in the pocket, the rest in the slab.</summary>
    private static int DepthInLayer(int y) => (WorldGenerationConfig.HellTopY - y) % Period;

    /// <summary>Whether a hell row lies in its layer's slab, which is hellrock whole.</summary>
    public static bool InSlab(int y) => InHell(y) && DepthInLayer(y) >= PocketHeight;

    /// <summary>The cavern noise a pocket cell of <paramref name="layer"/> must exceed away from the
    /// edges: lower, so more open, the deeper the layer.</summary>
    public static float OpenThreshold(int layer) =>
        MathF.Max(WorldGenerationConfig.HellOpenThresholdMin,
            WorldGenerationConfig.HellOpenThresholdTop - WorldGenerationConfig.HellOpenThresholdPerLayer * layer);

    /// <summary>How many cells above its pocket's floor <paramref name="layer"/>'s lava level stands:
    /// higher, so more lava, the deeper the layer.</summary>
    public static int LavaDepth(int layer) =>
        Math.Min(WorldGenerationConfig.HellLavaMaxDepth,
            WorldGenerationConfig.HellLavaBaseDepth + WorldGenerationConfig.HellLavaDepthPerLayer * layer);

    /// <summary>
    /// The cavern noise a cell of hell row <paramref name="y"/> must exceed to be open: the layer's
    /// <see cref="OpenThreshold"/> plus the edge bias, which rises quadratically to
    /// <see cref="WorldGenerationConfig.HellEdgeStrength"/> toward the pocket's ceiling and floor;
    /// infinite in the slab.
    /// </summary>
    public static float RowThreshold(int y)
    {
        var d = DepthInLayer(y);
        if (d >= PocketHeight) return float.PositiveInfinity;

        var edge = Math.Min(d, PocketHeight - 1 - d);
        var threshold = OpenThreshold(LayerIndex(y));
        if (edge >= WorldGenerationConfig.HellEdgeBlocks) return threshold;

        var t = 1f - (float)edge / WorldGenerationConfig.HellEdgeBlocks;
        return threshold + WorldGenerationConfig.HellEdgeStrength * t * t;
    }

    /// <summary>What an open cell of hell row <paramref name="y"/> holds: lava under its layer's lava
    /// level, air above it.</summary>
    private static byte OpenBlock(int y) =>
        PocketHeight - 1 - DepthInLayer(y) < LavaDepth(LayerIndex(y)) ? LavaBlock : BlockType.Air;

    /// <summary>The block hell generates at world cell (x, y, z), y at or below
    /// <see cref="WorldGenerationConfig.HellTopY"/>: exactly what <see cref="Stamp"/> writes there.</summary>
    public static byte BlockAt(int x, int y, int z)
    {
        if (!InHell(y)) throw new ArgumentOutOfRangeException(nameof(y), y, "above hell");

        var threshold = RowThreshold(y);
        if (threshold >= RowSolidLimit) return HellrockBlock;

        // Two's complement masking floors negatives too, so the base is the lattice point at or below
        var bx = x & ~Mask;
        var by = y & ~Mask;
        var bz = z & ~Mask;
        var field = OreNoiseLattice.Trilinear(
            Sample(bx, by, bz), Sample(bx, by, bz + Stride),
            Sample(bx, by + Stride, bz), Sample(bx, by + Stride, bz + Stride),
            Sample(bx + Stride, by, bz), Sample(bx + Stride, by, bz + Stride),
            Sample(bx + Stride, by + Stride, bz), Sample(bx + Stride, by + Stride, bz + Stride),
            Fraction(x), Fraction(y), Fraction(z));
        return field > threshold ? OpenBlock(y) : HellrockBlock;
    }

    /// <summary>
    /// Writes hell into every cell at or below <see cref="WorldGenerationConfig.HellTopY"/> of the chunk
    /// whose minimum corner is (worldX0, worldY0, worldZ0), whatever the cell held, and leaves the
    /// cells above untouched. Lattice points are sampled lazily, once each, and only for rows that
    /// can open.
    /// </summary>
    public static void Stamp(byte[] blocks, int worldX0, int worldY0, int worldZ0)
    {
        // NaN marks a lattice point not sampled yet; the noise never returns NaN
        Span<float> samples = stackalloc float[GridCount];
        samples.Fill(float.NaN);

        for (var y = 0; y < Chunk.Size; y++)
        {
            var wy = worldY0 + y;
            if (!InHell(wy)) break;

            var threshold = RowThreshold(wy);
            if (threshold >= RowSolidLimit)
            {
                FillRow(blocks, y, HellrockBlock);
                continue;
            }

            var open = OpenBlock(wy);
            var gy = y / Stride;
            var fy = Fraction(wy);
            for (var cx = 0; cx < Cells; cx++)
            for (var cz = 0; cz < Cells; cz++)
            {
                var c000 = Corner(samples, worldX0, worldY0, worldZ0, cx, gy, cz);
                var c001 = Corner(samples, worldX0, worldY0, worldZ0, cx, gy, cz + 1);
                var c010 = Corner(samples, worldX0, worldY0, worldZ0, cx, gy + 1, cz);
                var c011 = Corner(samples, worldX0, worldY0, worldZ0, cx, gy + 1, cz + 1);
                var c100 = Corner(samples, worldX0, worldY0, worldZ0, cx + 1, gy, cz);
                var c101 = Corner(samples, worldX0, worldY0, worldZ0, cx + 1, gy, cz + 1);
                var c110 = Corner(samples, worldX0, worldY0, worldZ0, cx + 1, gy + 1, cz);
                var c111 = Corner(samples, worldX0, worldY0, worldZ0, cx + 1, gy + 1, cz + 1);

                for (var x = cx * Stride; x < (cx + 1) * Stride; x++)
                {
                    var fx = Fraction(worldX0 + x);
                    for (var z = cz * Stride; z < (cz + 1) * Stride; z++)
                    {
                        var field = OreNoiseLattice.Trilinear(c000, c001, c010, c011, c100, c101, c110, c111,
                            fx, fy, Fraction(worldZ0 + z));
                        blocks[Chunk.Idx(x, y, z)] = field > threshold ? open : HellrockBlock;
                    }
                }
            }
        }
    }

    /// <summary>Every world y <see cref="Stamp"/> can write: all of hell, open-ended downward
    /// (<see cref="int.MinValue"/> stands for "no bottom").</summary>
    internal static StampBand WriteBand() => new(int.MinValue, WorldGenerationConfig.HellTopY);

    private static void FillRow(byte[] blocks, int y, byte block)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            blocks[Chunk.Idx(x, y, z)] = block;
    }

    /// <summary>The cavern noise at a lattice point.</summary>
    private static float Sample(int x, int y, int z) =>
        CavernNoise.GetNoise(x, y * WorldGenerationConfig.HellCavernYScale, z);

    /// <summary>The cell's center as a fraction of its lattice cell, the one expression both paths use.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Fraction(int v) => ((v & Mask) + 0.5f) * InvStride;

    /// <summary>The noise at lattice point (gx, gy, gz) of the chunk at world origin (worldX0, worldY0,
    /// worldZ0), sampled into <paramref name="samples"/> the first time it is asked for.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Corner(Span<float> samples, int worldX0, int worldY0, int worldZ0, int gx, int gy, int gz)
    {
        ref var slot = ref samples[gx * GridSq + gy * Grid + gz];
        if (float.IsNaN(slot))
            slot = Sample(worldX0 + gx * Stride, worldY0 + gy * Stride, worldZ0 + gz * Stride);
        return slot;
    }
}
