using System;
using System.Collections.Generic;
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
/// cell is open (<see cref="IsOpen"/>) where a 3D cavern noise, sampled on a world-aligned lattice every
/// <see cref="WorldGenerationConfig.HellNoiseStride"/> blocks and blended trilinearly at the cell's
/// center (the way <see cref="NoiseCaves"/> samples its fields), exceeds the row's threshold
/// (<see cref="RowThreshold"/>): the layer's openness, which grows with the layer index, plus an edge
/// bias that closes the cells near the pocket's ceiling and floor, so both follow the noise rather
/// than a plane. An open cell fewer than <see cref="LavaDepth"/> cells above its pocket's floor is a
/// pool cell (<see cref="InPool"/>): lava, or in a dry pool column of its biome (<see cref="HellBiomes"/>)
/// the biome's soil, so the floor's dips pond lava or fill flat. The level is flat across a layer and a
/// dry pool is solid, so every lava cell's sides and bottom are lava or solid and the pools never flow.
/// A row whose threshold no blend can pass (<see cref="RowSolidLimit"/>) is never open and never
/// samples the noise. The solid cells near open cavern wear their biome's skin (<see cref="Classify"/>):
/// floor, ceiling and wall blocks a few cells deep, decided from the open field around the cell.
/// A pure function of world position and seed: the point query <see cref="BlockAt"/> and
/// <see cref="Stamp"/> evaluate the same rows, lattice points and fraction expressions for the open
/// field and the same rule over it, so they agree bit for bit across every chunk seam, and a chunk
/// reads nothing outside itself.
/// </summary>
public static class HellGenerator
{
    private const int Stride = WorldGenerationConfig.HellNoiseStride;
    private const int Mask = Stride - 1;
    private const int Cells = Chunk.Size / Stride;   // lattice cells per chunk axis
    private const float InvStride = 1f / Stride;

    private const int Period = WorldGenerationConfig.HellLayerPeriodBlocks;

    private const int FloorSkin = WorldGenerationConfig.HellFloorSkinBlocks;
    private const int CeilingSkin = WorldGenerationConfig.HellCeilingSkinBlocks;

    // How far above a cell the rules read: the floor skin's scan, or the flora's vine scan
    private const int AboveReach = FloorSkin > HellFlora.ReachUp ? FloorSkin : HellFlora.ReachUp;

    // The stamp's open mask: the chunk plus a one-cell ring horizontally (the wall test), the ceiling
    // skin's reach below and the floor skin's or the vines' reach above
    private const int MaskSide = Chunk.Size + 2;
    private const int MaskRows = Chunk.Size + CeilingSkin + AboveReach;
    private const int MaskCount = MaskSide * MaskSide * MaskRows;

    // The lattice points the mask reads: x and z from -1 to Cells + 1, y from GyMin to GyMax
    private const int PointsH = Cells + 3;
    private const int GyMin = -((CeilingSkin + Stride - 1) / Stride);
    private const int GyMax = (Chunk.Size - 1 + AboveReach) / Stride + 1;
    private const int PointsV = GyMax - GyMin + 1;
    private const int PointCount = PointsH * PointsH * PointsV;

    // The stamp's per-row rule flags
    private const byte SkinRow = 1;
    private const byte PoolRow = 2;

    // The stamp's per-column biome and pool caches: two layers of chunk columns, Unknown until read
    private const int ColumnSlots = 2 * Chunk.Size * Chunk.Size;
    private const byte Unknown = byte.MaxValue;

    // The stamp's cache of the biome and pool lattice points over the chunk, per layer slot
    private const int BiomePoints = Chunk.Size / HellBiomes.BiomeStride + 1;
    private const int PoolPoints = Chunk.Size / HellBiomes.PoolStride + 1;
    private const int LayerLatticeSlots = BiomePoints * BiomePoints + PoolPoints * PoolPoints;

    /// <summary>Rows of open cavern at the top of every layer, above its slab.</summary>
    public const int PocketHeight = Period - WorldGenerationConfig.HellSlabThicknessBlocks;

    /// <summary>A row whose threshold is at or above this is never open: the two-octave cavern noise
    /// is normalized to about -1..1, so no blend passes it anyway, and the rule lets such rows (the
    /// slab, the pocket's outermost rows) skip the noise.</summary>
    public const float RowSolidLimit = 1f;

    private static FastNoiseLite CavernNoise;

    /// <summary>The hellrock block; resolved once in the static constructor.</summary>
    public static readonly ushort HellrockBlock;

    /// <summary>The lava block the pools are made of.</summary>
    public static readonly ushort LavaBlock;

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

    /// <summary>Whether hell row <paramref name="y"/> lies under its layer's lava level: an open cell
    /// there is a pool cell (lava, or the biome's soil where its pool is dry), never air.</summary>
    public static bool InPool(int y)
    {
        if (!InHell(y)) return false;
        var d = DepthInLayer(y);
        return d < PocketHeight && PocketHeight - 1 - d < LavaDepth(LayerIndex(y));
    }

    /// <summary>Whether hell cell (x, y, z) is open cavern: air, or a pool cell (lava, or a dry
    /// pool's soil). The one field both <see cref="BlockAt"/> and <see cref="Stamp"/> read; false
    /// above hell and in every row that cannot open.</summary>
    public static bool IsOpen(int x, int y, int z)
    {
        if (!RowCanOpen(y)) return false;

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
        return field > RowThreshold(y);
    }

    /// <summary><see cref="IsOpen(int, int, int)"/>, the same expression, with each lattice sample
    /// read through <paramref name="samples"/> (keyed by <see cref="CellKey"/>), so a caller asking
    /// about many nearby cells (a hell tree's growth, a floor scan) samples each lattice point once.</summary>
    internal static bool IsOpen(int x, int y, int z, Dictionary<long, float> samples)
    {
        if (!RowCanOpen(y)) return false;

        var bx = x & ~Mask;
        var by = y & ~Mask;
        var bz = z & ~Mask;
        var field = OreNoiseLattice.Trilinear(
            Sample(samples, bx, by, bz), Sample(samples, bx, by, bz + Stride),
            Sample(samples, bx, by + Stride, bz), Sample(samples, bx, by + Stride, bz + Stride),
            Sample(samples, bx + Stride, by, bz), Sample(samples, bx + Stride, by, bz + Stride),
            Sample(samples, bx + Stride, by + Stride, bz), Sample(samples, bx + Stride, by + Stride, bz + Stride),
            Fraction(x), Fraction(y), Fraction(z));
        return field > RowThreshold(y);
    }

    /// <summary>Whether hell cell (x, y, z) is open cavern above its layer's lava level: the air the
    /// pocket leaves before any flora (<see cref="HellFlora"/>) or tree (<see cref="HellTrees"/>).</summary>
    internal static bool IsCavernAir(int x, int y, int z, Dictionary<long, float> samples) =>
        !InPool(y) && IsOpen(x, y, z, samples);

    /// <summary>The block hell generates at world cell (x, y, z), y at or below
    /// <see cref="WorldGenerationConfig.HellTopY"/>: exactly what <see cref="Stamp"/> writes there.</summary>
    public static ushort BlockAt(int x, int y, int z)
    {
        if (!InHell(y)) throw new ArgumentOutOfRangeException(nameof(y), y, "above hell");
        if (!NeedsClassify(y)) return HellrockBlock;

        var field = new PointField();
        return Classify(ref field, x, y, z);
    }

    /// <summary>
    /// Writes hell into every cell at or below <see cref="WorldGenerationConfig.HellTopY"/> of the chunk
    /// whose minimum corner is (worldX0, worldY0, worldZ0), whatever the cell held, and leaves the
    /// cells above untouched. The open field is computed once over the chunk plus a one-cell ring and
    /// the skin reach above and below, from lattice points sampled lazily, once each, and only for rows
    /// that can open; every cell is then classified exactly as <see cref="BlockAt"/> classifies it.
    /// </summary>
    public static void Stamp(ushort[] blocks, int worldX0, int worldY0, int worldZ0)
    {
        var anyClassified = false;
        for (var y = 0; y < Chunk.Size && InHell(worldY0 + y); y++)
            anyClassified |= NeedsClassify(worldY0 + y);

        if (!anyClassified)
        {
            for (var y = 0; y < Chunk.Size && InHell(worldY0 + y); y++)
                FillRow(blocks, y, HellrockBlock);
            return;
        }

        Span<bool> mask = stackalloc bool[MaskCount];
        mask.Clear();
        FillOpenMask(mask, worldX0, worldY0, worldZ0);

        Span<byte> rows = stackalloc byte[MaskRows];
        for (var r = 0; r < MaskRows; r++)
            rows[r] = RowFlags(worldY0 - CeilingSkin + r);

        Span<byte> biomes = stackalloc byte[ColumnSlots];
        Span<byte> pools = stackalloc byte[ColumnSlots];
        Span<float> lattice = stackalloc float[2 * LayerLatticeSlots];
        biomes.Fill(Unknown);
        pools.Fill(Unknown);
        lattice.Fill(float.NaN);
        var field = new ChunkField(mask, rows, biomes, pools, lattice, worldX0, worldY0, worldZ0);

        for (var y = 0; y < Chunk.Size; y++)
        {
            var wy = worldY0 + y;
            if (!InHell(wy)) break;
            if (!NeedsClassify(wy))
            {
                FillRow(blocks, y, HellrockBlock);
                continue;
            }

            for (var x = 0; x < Chunk.Size; x++)
            for (var z = 0; z < Chunk.Size; z++)
                blocks[Chunk.Idx(x, y, z)] = Classify(ref field, worldX0 + x, wy, worldZ0 + z);
        }
    }

    /// <summary>Every world y <see cref="Stamp"/> can write: all of hell, open-ended downward
    /// (<see cref="int.MinValue"/> stands for "no bottom").</summary>
    internal static StampBand WriteBand() => new(int.MinValue, WorldGenerationConfig.HellTopY);

    /// <summary>Whether a hell row can hold an open cell at all: its threshold is under
    /// <see cref="RowSolidLimit"/>. False above hell.</summary>
    internal static bool RowCanOpen(int y) => InHell(y) && RowThreshold(y) < RowSolidLimit;

    /// <summary>Whether a solid cell of row <paramref name="y"/> can wear a skin: it lies in a pocket
    /// and a row within the skin reach above or below it can open.</summary>
    internal static bool CanWearSkin(int y)
    {
        if (InSlab(y)) return false;
        for (var r = y - CeilingSkin; r <= y + FloorSkin; r++)
            if (RowCanOpen(r)) return true;
        return false;
    }

    /// <summary>The stamp's cached row rules: <see cref="CanWearSkin"/> and <see cref="InPool"/> as flags.</summary>
    private static byte RowFlags(int y) =>
        (byte)((CanWearSkin(y) ? SkinRow : 0) | (InPool(y) ? PoolRow : 0));

    /// <summary>Whether a row needs <see cref="Classify"/>; every other hell row is hellrock whole.</summary>
    private static bool NeedsClassify(int y) => RowCanOpen(y) || CanWearSkin(y);

    /// <summary>
    /// The one rule every hell cell follows, over either open field. An open cell is what
    /// <see cref="OpenCell"/> says. A solid pocket cell with open cavern within <see cref="FloorSkin"/>
    /// above it is floor (its biome's surface right under air, soil deeper, the pool bed under lava);
    /// else with open cavern within <see cref="CeilingSkin"/> under it ceiling (now and then the
    /// biome's ceiling light right over open cavern); else, above the lava level, beside open cavern
    /// wall; anything else hellrock.
    /// </summary>
    private static ushort Classify<TField>(ref TField field, int x, int y, int z)
        where TField : IHellField, allows ref struct
    {
        if (field.IsOpen(x, y, z)) return OpenCell(ref field, x, y, z);
        if (!field.CanWearSkin(y)) return HellrockBlock;

        for (var d = 1; d <= FloorSkin; d++)
        {
            if (!field.IsOpen(x, y + d, z)) continue;
            var palette = PaletteAt(ref field, x, y, z);
            if (field.InPool(y + d)) return field.PoolIsLava(x, LayerIndex(y + d), z) ? palette.PoolBed : palette.Soil;
            return d == 1 ? Surface(palette, x, y, z) : palette.Soil;
        }

        for (var d = 1; d <= CeilingSkin; d++)
        {
            if (!field.IsOpen(x, y - d, z)) continue;
            var palette = PaletteAt(ref field, x, y, z);
            return d == 1 && HellBiomes.Picks(x, y, z, palette.CeilingLightRate) ? palette.CeilingLight : palette.Ceiling;
        }

        // A pool row's open cells are pool cells, never air, so it has no walls to show
        if (field.InPool(y)) return HellrockBlock;
        if (!field.IsOpen(x - 1, y, z) && !field.IsOpen(x + 1, y, z)
            && !field.IsOpen(x, y, z - 1) && !field.IsOpen(x, y, z + 1)) return HellrockBlock;
        return PaletteAt(ref field, x, y, z).Wall;
    }

    /// <summary>What open cell (x, y, z) holds: above its layer's lava level air or the flora
    /// <see cref="HellFlora.AirCell"/> puts there; under it lava where the column's pool holds lava,
    /// else the biome's soil, its top row the biome's surface.</summary>
    private static ushort OpenCell<TField>(ref TField field, int x, int y, int z)
        where TField : IHellField, allows ref struct
    {
        if (!field.InPool(y)) return HellFlora.AirCell(ref field, x, y, z);

        var layer = LayerIndex(y);
        if (field.PoolIsLava(x, layer, z)) return LavaBlock;
        var palette = HellBiomes.Palette(field.Biome(x, layer, z));
        return PocketHeight - DepthInLayer(y) == LavaDepth(layer) ? Surface(palette, x, y, z) : palette.Soil;
    }

    private static HellBiomePalette PaletteAt<TField>(ref TField field, int x, int y, int z)
        where TField : IHellField, allows ref struct =>
        HellBiomes.Palette(field.Biome(x, LayerIndex(y), z));

    /// <summary>A floor's top cell: the biome's surface, now and then its floor light.</summary>
    internal static ushort Surface(HellBiomePalette palette, int x, int y, int z) =>
        palette.FloorLight != BlockType.Air && HellBiomes.Picks(x, y, z, palette.FloorLightRate)
            ? palette.FloorLight
            : palette.Surface;

    /// <summary>
    /// Fills <paramref name="mask"/> with <see cref="IsOpen"/> over the chunk at world origin
    /// (worldX0, worldY0, worldZ0) grown by one cell on each horizontal side and by the skin reach
    /// above and below: the same lattice points, rows, thresholds and fraction expressions, per
    /// lattice cell.
    /// </summary>
    private static void FillOpenMask(Span<bool> mask, int worldX0, int worldY0, int worldZ0)
    {
        // NaN marks a lattice point not sampled yet; the noise never returns NaN
        Span<float> samples = stackalloc float[PointCount];
        samples.Fill(float.NaN);

        for (var r = 0; r < MaskRows; r++)
        {
            var wy = worldY0 - CeilingSkin + r;
            if (!RowCanOpen(wy)) continue;

            var threshold = RowThreshold(wy);
            var gy = ((wy & ~Mask) - worldY0) / Stride;
            var fy = Fraction(wy);
            for (var cx = -1; cx <= Cells; cx++)
            for (var cz = -1; cz <= Cells; cz++)
            {
                var c000 = Corner(samples, worldX0, worldY0, worldZ0, cx, gy, cz);
                var c001 = Corner(samples, worldX0, worldY0, worldZ0, cx, gy, cz + 1);
                var c010 = Corner(samples, worldX0, worldY0, worldZ0, cx, gy + 1, cz);
                var c011 = Corner(samples, worldX0, worldY0, worldZ0, cx, gy + 1, cz + 1);
                var c100 = Corner(samples, worldX0, worldY0, worldZ0, cx + 1, gy, cz);
                var c101 = Corner(samples, worldX0, worldY0, worldZ0, cx + 1, gy, cz + 1);
                var c110 = Corner(samples, worldX0, worldY0, worldZ0, cx + 1, gy + 1, cz);
                var c111 = Corner(samples, worldX0, worldY0, worldZ0, cx + 1, gy + 1, cz + 1);

                var xEnd = Math.Min(cx * Stride + Mask, Chunk.Size);
                var zEnd = Math.Min(cz * Stride + Mask, Chunk.Size);
                for (var x = Math.Max(cx * Stride, -1); x <= xEnd; x++)
                {
                    var fx = Fraction(worldX0 + x);
                    var row = (r * MaskSide + x + 1) * MaskSide + 1;
                    for (var z = Math.Max(cz * Stride, -1); z <= zEnd; z++)
                    {
                        var value = OreNoiseLattice.Trilinear(c000, c001, c010, c011, c100, c101, c110, c111,
                            fx, fy, Fraction(worldZ0 + z));
                        mask[row + z] = value > threshold;
                    }
                }
            }
        }
    }

    private static void FillRow(ushort[] blocks, int y, ushort block)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            blocks[Chunk.Idx(x, y, z)] = block;
    }

    /// <summary>The cavern noise at a lattice point.</summary>
    private static float Sample(int x, int y, int z) =>
        CavernNoise.GetNoise(x, y * WorldGenerationConfig.HellCavernYScale, z);

    /// <summary>The cavern noise at a lattice point, sampled into <paramref name="samples"/> the first
    /// time it is asked for.</summary>
    private static float Sample(Dictionary<long, float> samples, int x, int y, int z)
    {
        var key = CellKey.Pack(x, y, z);
        if (samples.TryGetValue(key, out var value)) return value;
        value = Sample(x, y, z);
        samples[key] = value;
        return value;
    }

    /// <summary>The cell's center as a fraction of its lattice cell, the one expression both paths use.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Fraction(int v) => ((v & Mask) + 0.5f) * InvStride;

    /// <summary>The noise at lattice point (gx, gy, gz) of the chunk at world origin (worldX0, worldY0,
    /// worldZ0), sampled into <paramref name="samples"/> the first time it is asked for. gx and gz run
    /// from -1 to <see cref="Cells"/> + 1, gy from <see cref="GyMin"/> to <see cref="GyMax"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Corner(Span<float> samples, int worldX0, int worldY0, int worldZ0, int gx, int gy, int gz)
    {
        ref var slot = ref samples[((gx + 1) * PointsV + gy - GyMin) * PointsH + gz + 1];
        if (float.IsNaN(slot))
            slot = Sample(worldX0 + gx * Stride, worldY0 + gy * Stride, worldZ0 + gz * Stride);
        return slot;
    }

    /// <summary>The point query's field: everything computed on the spot.</summary>
    private readonly struct PointField : IHellField
    {
        public bool IsOpen(int x, int y, int z) => HellGenerator.IsOpen(x, y, z);
        public bool CanWearSkin(int y) => HellGenerator.CanWearSkin(y);
        public bool InPool(int y) => HellGenerator.InPool(y);
        public HellBiome Biome(int x, int layer, int z) => HellBiomes.AtLayer(x, layer, z);
        public bool PoolIsLava(int x, int layer, int z) => HellBiomes.PoolIsLava(x, layer, z, Biome(x, layer, z));
    }

    /// <summary>The stamp's field: the open mask filled by <see cref="FillOpenMask"/>, the row rules of
    /// every mask row (<see cref="RowFlags"/>), and the biome and pool of each chunk column per layer,
    /// each worked out once from lattice points sampled once, with <see cref="HellBiomes"/>' own blend
    /// and rules. A chunk's queries span at most two layers (the skin reach is far under a layer's
    /// height), the first being its topmost queried row's.</summary>
    private readonly ref struct ChunkField(Span<bool> mask, Span<byte> rows, Span<byte> biomes, Span<byte> pools,
        Span<float> lattice, int worldX0, int worldY0, int worldZ0) : IHellField
    {
        private readonly Span<bool> _mask = mask;
        private readonly Span<byte> _rows = rows;
        private readonly Span<byte> _biomes = biomes;
        private readonly Span<byte> _pools = pools;
        private readonly Span<float> _lattice = lattice;
        private readonly int _x0 = worldX0;
        private readonly int _y0 = worldY0;
        private readonly int _z0 = worldZ0;
        private readonly int _firstLayer =
            LayerIndex(Math.Min(worldY0 + Chunk.Size - 1 + AboveReach, WorldGenerationConfig.HellTopY));

        public bool IsOpen(int x, int y, int z) =>
            _mask[((y - _y0 + CeilingSkin) * MaskSide + x - _x0 + 1) * MaskSide + z - _z0 + 1];

        public bool CanWearSkin(int y) => (_rows[y - _y0 + CeilingSkin] & SkinRow) != 0;
        public bool InPool(int y) => (_rows[y - _y0 + CeilingSkin] & PoolRow) != 0;

        public HellBiome Biome(int x, int layer, int z)
        {
            ref var slot = ref _biomes[Slot(x, layer, z)];
            if (slot != Unknown) return (HellBiome)slot;

            const int stride = HellBiomes.BiomeStride;
            var gx = (x - _x0) / stride;
            var gz = (z - _z0) / stride;
            var biome = HellBiomes.Classify(HellBiomes.Bilerp(
                BiomePoint(layer, gx, gz), BiomePoint(layer, gx, gz + 1),
                BiomePoint(layer, gx + 1, gz), BiomePoint(layer, gx + 1, gz + 1), x, z, stride), layer);
            slot = (byte)biome;
            return biome;
        }

        public bool PoolIsLava(int x, int layer, int z)
        {
            ref var slot = ref _pools[Slot(x, layer, z)];
            if (slot != Unknown) return slot == 1;

            var biome = Biome(x, layer, z);
            if (!HellBiomes.PoolShareDecides(biome, out var lava))
            {
                const int stride = HellBiomes.PoolStride;
                var gx = (x - _x0) / stride;
                var gz = (z - _z0) / stride;
                lava = HellBiomes.PoolUnder(biome, HellBiomes.Bilerp(
                    PoolPoint(layer, gx, gz), PoolPoint(layer, gx, gz + 1),
                    PoolPoint(layer, gx + 1, gz), PoolPoint(layer, gx + 1, gz + 1), x, z, stride));
            }

            slot = lava ? (byte)1 : (byte)0;
            return lava;
        }

        private float BiomePoint(int layer, int gx, int gz)
        {
            ref var point = ref _lattice[(layer - _firstLayer) * LayerLatticeSlots + gx * BiomePoints + gz];
            if (float.IsNaN(point))
                point = HellBiomes.BiomeSample(_x0 + gx * HellBiomes.BiomeStride, layer, _z0 + gz * HellBiomes.BiomeStride);
            return point;
        }

        private float PoolPoint(int layer, int gx, int gz)
        {
            ref var point = ref _lattice[(layer - _firstLayer) * LayerLatticeSlots + BiomePoints * BiomePoints + gx * PoolPoints + gz];
            if (float.IsNaN(point))
                point = HellBiomes.PoolSample(_x0 + gx * HellBiomes.PoolStride, layer, _z0 + gz * HellBiomes.PoolStride);
            return point;
        }

        private int Slot(int x, int layer, int z) => ((layer - _firstLayer) * Chunk.Size + x - _x0) * Chunk.Size + z - _z0;
    }
}
