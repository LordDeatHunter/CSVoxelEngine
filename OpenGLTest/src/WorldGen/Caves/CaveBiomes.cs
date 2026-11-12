using System;
using System.Runtime.CompilerServices;
using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.WorldGen.Features;
using VoxelEngine.WorldGen.Features.Underground;
using VoxelEngine.WorldGen.Hell;

namespace VoxelEngine.WorldGen.Caves;

/// <summary>
/// Which <see cref="CaveBiome"/> an overworld rock cell belongs to (plans/CAVE_BIOMES.md CB-1), and the
/// palette pass that dresses the carved caves in it. Three coherent 3D noises (seed offsets 179, 180, 181)
/// are sampled on a world-aligned lattice of <see cref="WorldGenerationConfig.CaveBiomeLatticeStride"/>
/// blocks and blended trilinearly at the cell's center (<see cref="OreNoiseLattice.Trilinear"/>): an echo
/// drift and a deep drift that make the band edges wavy, and a selector that picks the sub-biome. The one
/// rule <see cref="Classify"/> turns them, the cell's height, its depth under its column's top and the
/// column's stored surface biome into a cave biome:
/// <list type="bullet">
/// <item>the echo band, from a jittered cover under the column top down to a wavy line near
/// <see cref="WorldGenerationConfig.CaveEchoBottomY"/>: a frozen column's caves echo it in frost stone,
/// a desert column's in sandstone, every other column's stay plain;</item>
/// <item>under it, down to the barrier: the magma-touched deep under a second wavy line near
/// <see cref="WorldGenerationConfig.CaveDeepTopY"/>, and above that crystal, plain or fungal by the
/// selector;</item>
/// <item>nothing at or below <see cref="WorldGenerationConfig.BarrierTopY"/>, so the barrier and hell are
/// untouched.</item>
/// </list>
/// <see cref="At"/> and <see cref="Fill"/> read the same lattice points, use the same fraction
/// expressions and call the same <see cref="Classify"/>, so they agree bit for bit across every chunk
/// seam. <see cref="Fill"/> classifies a chunk once into a <see cref="CaveBiomeMap"/> (CB-2) that every
/// cave consumer reads (the noise cave shape, the ore richness, the exclusive ores) and that
/// <see cref="Stamp(ushort[], CaveBiomeMap, int, int, int)"/> dresses the caves from. A pure function of
/// position, the column's stored biome and height, and the seed. CaveEchoVolumes then extends echo
/// materials laterally and vertically with the same point/map containment; thread-safe: the noises are read-only
/// after their reseed, and all scratch lives on the stack or in the caller's map.
/// </summary>
public static class CaveBiomes
{
    /// <summary>Lattice spacing in blocks (<see cref="WorldGenerationConfig.CaveBiomeLatticeStride"/>).</summary>
    internal const int Stride = WorldGenerationConfig.CaveBiomeLatticeStride;

    private const int Mask = Stride - 1;
    private const int Cells = Chunk.Size / Stride;   // lattice cells per chunk axis
    private const int Grid = Cells + 1;              // lattice points per chunk axis, both faces included
    private const int GridSq = Grid * Grid;
    private const int GridCount = GridSq * Grid;
    private const int CellCount = Cells * Cells * Cells;
    private const int CornersPerCell = 3 * 8;        // three noises, eight corners each
    private const float InvStride = 1f / Stride;

    // How far a trilinear blend may stray past its corners' range by float rounding, widened a lot:
    // the ranges Stamp decides a cell by only have to contain the exact blend
    private const float BoundSlack = 1e-4f;

    /// <summary>The highest y a sub-biome can reach: the echo band's bottom at its highest drift.</summary>
    internal const int SubBandTopY = WorldGenerationConfig.CaveEchoBottomY + WorldGenerationConfig.CaveEchoDriftBlocks;

    // Salt of the accent scatter, so it never lines up with another feature hashing the same cell
    private const ulong AccentSalt = 0x43415645UL;

    private static FastNoiseLite EchoDriftNoise;
    private static FastNoiseLite DeepDriftNoise;
    private static FastNoiseLite SelectorNoise;

    private static readonly CaveBiomePalette[] Palettes;

    /// <summary>The block the pass hosts on: plain stone and nothing else.</summary>
    private static readonly ushort RockBlock;

    static CaveBiomes()
    {
        if (Stride <= 0 || (Stride & (Stride - 1)) != 0 || Chunk.Size % Stride != 0)
            throw new InvalidOperationException(
                $"CaveBiomeLatticeStride {Stride} must be a positive power of two dividing the chunk size {Chunk.Size}.");

        Reseed();

        ushort Block(string name) => ContentRegistry.RequireBlock(name).Id;
        var stone = Block("stone");
        var frostStone = Block("frost_stone");
        var sandstone = Block("sandstone");
        var scorched = Block("scorched_hellrock");
        RockBlock = stone;

        Palettes = new CaveBiomePalette[Enum.GetValues<CaveBiome>().Length];
        Palettes[(int)CaveBiome.Plain] = new CaveBiomePalette(stone, stone, stone, stone, 0);
        Palettes[(int)CaveBiome.FrozenEcho] = new CaveBiomePalette(
            Stone: frostStone, Floor: Block("permafrost"), Ceiling: frostStone, Accent: frostStone, AccentRate: 0);
        Palettes[(int)CaveBiome.DesertEcho] = new CaveBiomePalette(
            Stone: sandstone, Floor: sandstone, Ceiling: sandstone, Accent: sandstone, AccentRate: 0);
        Palettes[(int)CaveBiome.Crystal] = new CaveBiomePalette(
            Stone: stone, Floor: stone, Ceiling: stone, Accent: Block("crystal_block"),
            AccentRate: WorldGenerationConfig.CaveCrystalAccentRate);
        Palettes[(int)CaveBiome.Fungal] = new CaveBiomePalette(
            Stone: stone, Floor: Block("fungal_moss"), Ceiling: Block("fungal_soil"), Accent: Block("fungal_glow"),
            AccentRate: WorldGenerationConfig.CaveFungalAccentRate);
        Palettes[(int)CaveBiome.MagmaDeep] = new CaveBiomePalette(
            Stone: scorched, Floor: scorched, Ceiling: scorched, Accent: Block("magma_block"),
            AccentRate: WorldGenerationConfig.CaveMagmaAccentRate);
    }

    /// <summary>Builds the noises from the current seed (static constructor and <see cref="WorldSeed.Apply"/>).</summary>
    internal static void Reseed()
    {
        EchoDriftNoise = TerrainGenerator.MakeNoise(179, WorldGenerationConfig.CaveEchoDriftFrequency);
        DeepDriftNoise = TerrainGenerator.MakeNoise(180, WorldGenerationConfig.CaveDeepDriftFrequency);
        SelectorNoise = TerrainGenerator.MakeNoise(181, WorldGenerationConfig.CaveSubBiomeFrequency, octaves: 2);
    }

    /// <summary>The palette of <paramref name="biome"/>.</summary>
    public static CaveBiomePalette Palette(CaveBiome biome) => Palettes[(int)biome];

    /// <summary>
    /// The cave biome of world cell (x, y, z) in a column whose stored surface biome is
    /// <paramref name="surfaceBiome"/> and whose top is <paramref name="surfaceHeight"/>
    /// (<see cref="ColumnData.Biomes"/> and <see cref="ColumnData.Heights"/>): exactly what
    /// <see cref="Fill"/> writes for that cell.
    /// </summary>
    public static CaveBiome At(int x, int y, int z, Biome surfaceBiome, int surfaceHeight)
    {
        // Two's complement masking floors negatives too, so the base is the lattice point at or below
        var bx = x & ~Mask;
        var by = y & ~Mask;
        var bz = z & ~Mask;
        var fx = ((x & Mask) + 0.5f) * InvStride;
        var fy = ((y & Mask) + 0.5f) * InvStride;
        var fz = ((z & Mask) + 0.5f) * InvStride;
        var original = Classify(y, surfaceHeight - y, surfaceBiome,
            Blend(EchoDriftNoise, bx, by, bz, fx, fy, fz),
            Blend(DeepDriftNoise, bx, by, bz, fx, fy, fz),
            Blend(SelectorNoise, bx, by, bz, fx, fy, fz));
        return CaveEchoVolumes.At(x, y, z, surfaceHeight, original);
    }

    /// <summary>
    /// The one cave-biome rule: the biome of a cell at world y <paramref name="y"/>,
    /// <paramref name="depth"/> blocks under its column's top, whose column's surface biome is
    /// <paramref name="surface"/>, given the three blended noise values (each clamped to -1..1 here, so
    /// <see cref="WriteBand"/>'s bounds hold whatever the noise range).
    /// </summary>
    internal static CaveBiome Classify(int y, int depth, Biome surface, float drift, float deepDrift, float selector)
    {
        drift = Math.Clamp(drift, -1f, 1f);
        deepDrift = Math.Clamp(deepDrift, -1f, 1f);
        selector = Math.Clamp(selector, -1f, 1f);

        if (y <= WorldGenerationConfig.BarrierTopY) return CaveBiome.Plain;

        if (y > EchoLine(drift))
            return depth < CoverDepth(drift) ? CaveBiome.Plain : EchoOf(surface);

        return y <= DeepLine(deepDrift) ? CaveBiome.MagmaDeep : SelectorBiome(selector);
    }

    /// <summary>
    /// What <see cref="Classify"/> gives every cell of one lattice cell that its corner ranges already
    /// decide. A trilinear blend lies within its corners' range (widened by <see cref="BoundSlack"/> for
    /// rounding), and every line of the rule is a monotone float expression of its clamped noise
    /// (rounding never reverses an order), so the lines at the range ends bracket the cell's exact lines.
    /// </summary>
    private struct CellRule
    {
        /// <summary>The echo line at the lowest and highest drift: above EchoHi a cell is surely in the echo
        /// band, at or below EchoLo surely under it.</summary>
        public float EchoLo, EchoHi;

        /// <summary>The cover at the lowest and highest drift.</summary>
        public float CoverLo, CoverHi;

        /// <summary>The deep line at the lowest and highest deep drift.</summary>
        public float DeepLo, DeepHi;

        /// <summary>The sub-biome the whole selector range picks, when <see cref="SelectorDecided"/>.</summary>
        public CaveBiome Selected;

        /// <summary>Whether the whole selector range lies on one side of both selector thresholds.</summary>
        public bool SelectorDecided;

        /// <summary>The rule of a lattice cell whose blends lie in the given ranges.</summary>
        public static CellRule From(float driftLo, float driftHi, float deepLo, float deepHi,
            float selectorLo, float selectorHi)
        {
            driftLo = Math.Clamp(driftLo, -1f, 1f);
            driftHi = Math.Clamp(driftHi, -1f, 1f);
            selectorLo = Math.Clamp(selectorLo, -1f, 1f);
            selectorHi = Math.Clamp(selectorHi, -1f, 1f);
            var rule = new CellRule
            {
                EchoLo = EchoLine(driftLo),
                EchoHi = EchoLine(driftHi),
                CoverLo = CoverDepth(driftLo),
                CoverHi = CoverDepth(driftHi),
                DeepLo = DeepLine(Math.Clamp(deepLo, -1f, 1f)),
                DeepHi = DeepLine(Math.Clamp(deepHi, -1f, 1f)),
            };

            if (selectorLo > WorldGenerationConfig.CaveCrystalMinValue)
                (rule.Selected, rule.SelectorDecided) = (CaveBiome.Crystal, true);
            else if (selectorHi < WorldGenerationConfig.CaveFungalMaxValue)
                (rule.Selected, rule.SelectorDecided) = (CaveBiome.Fungal, true);
            else if (selectorHi <= WorldGenerationConfig.CaveCrystalMinValue
                     && selectorLo >= WorldGenerationConfig.CaveFungalMaxValue)
                (rule.Selected, rule.SelectorDecided) = (CaveBiome.Plain, true);
            return rule;
        }

        /// <summary>
        /// <see cref="Classify"/> of a cell above the barrier at world y <paramref name="y"/>,
        /// <paramref name="depth"/> under its column's top, in a column whose echo is
        /// <paramref name="echo"/>: true with its biome, false when the ranges straddle a line.
        /// </summary>
        public readonly bool TryDecide(int y, int depth, CaveBiome echo, out CaveBiome biome)
        {
            biome = CaveBiome.Plain;
            if (y > EchoHi)
            {
                if (echo == CaveBiome.Plain || depth < CoverLo) return true;
                if (depth < CoverHi) return false;
                biome = echo;
                return true;
            }

            if (y > EchoLo) return false;
            if (y <= DeepLo)
            {
                biome = CaveBiome.MagmaDeep;
                return true;
            }

            if (y <= DeepHi || !SelectorDecided) return false;
            biome = Selected;
            return true;
        }
    }

    /// <summary>The echo band's bottom at a clamped echo drift: echo cells lie strictly above it.</summary>
    private static float EchoLine(float drift) =>
        WorldGenerationConfig.CaveEchoBottomY + WorldGenerationConfig.CaveEchoDriftBlocks * drift;

    /// <summary>The cover an echo cell needs under its column's top at a clamped echo drift.</summary>
    private static float CoverDepth(float drift) =>
        WorldGenerationConfig.CaveEchoCoverBlocks + WorldGenerationConfig.CaveEchoCoverJitterBlocks * (drift + 1f) * 0.5f;

    /// <summary>The magma-touched deep's top at a clamped deep drift: deep cells lie at or below it.</summary>
    private static float DeepLine(float deepDrift) =>
        WorldGenerationConfig.CaveDeepTopY + WorldGenerationConfig.CaveDeepDriftBlocks * deepDrift;

    /// <summary>The cave echo of a surface biome; plain for a biome without one.</summary>
    internal static CaveBiome EchoOf(Biome surface) => surface switch
    {
        Biome.Frozen or Biome.Taiga => CaveBiome.FrozenEcho,
        Biome.Desert => CaveBiome.DesertEcho,
        _ => CaveBiome.Plain,
    };

    /// <summary>
    /// Where chunk-local rock cell (lx, ly, lz) sits against the open cave, read from the chunk's own
    /// cells only (a neighbor outside the chunk counts as closed): a floor with a non-occluding cell
    /// above, else a ceiling with one below, else a wall with one beside, else interior.
    /// </summary>
    internal static CaveCellRole RoleAt(ushort[] blocks, int lx, int ly, int lz)
    {
        if (ly < Chunk.SizeMask && !BlockType.Occludes(blocks[Chunk.Idx(lx, ly + 1, lz)])) return CaveCellRole.Floor;
        if (ly > 0 && !BlockType.Occludes(blocks[Chunk.Idx(lx, ly - 1, lz)])) return CaveCellRole.Ceiling;
        if (lx > 0 && !BlockType.Occludes(blocks[Chunk.Idx(lx - 1, ly, lz)])) return CaveCellRole.Wall;
        if (lx < Chunk.SizeMask && !BlockType.Occludes(blocks[Chunk.Idx(lx + 1, ly, lz)])) return CaveCellRole.Wall;
        if (lz > 0 && !BlockType.Occludes(blocks[Chunk.Idx(lx, ly, lz - 1)])) return CaveCellRole.Wall;
        if (lz < Chunk.SizeMask && !BlockType.Occludes(blocks[Chunk.Idx(lx, ly, lz + 1)])) return CaveCellRole.Wall;
        return CaveCellRole.Interior;
    }

    /// <summary>
    /// The block a rock cell of <paramref name="biome"/> in <paramref name="role"/> at world cell
    /// (wx, wy, wz) wears: the accent on one cave face cell in the palette's accent rate (by a hash of
    /// the cell and the seed), else the floor, ceiling or base stone.
    /// </summary>
    internal static ushort BlockFor(CaveBiome biome, CaveCellRole role, int wx, int wy, int wz)
    {
        var p = Palettes[(int)biome];
        if (role != CaveCellRole.Interior && p.AccentRate > 0
            && HellBiomes.Hash(wx, wy, wz, AccentSalt) % (ulong)p.AccentRate == 0)
            return p.Accent;

        return role switch
        {
            CaveCellRole.Floor => p.Floor,
            CaveCellRole.Ceiling => p.Ceiling,
            _ => p.Stone,
        };
    }

    /// <summary>
    /// The world y band in which <see cref="Fill"/> can find a non-plain cell, and so the band the palette
    /// pass can write, over <paramref name="column"/>, from
    /// <see cref="Classify"/>'s bounds with its clamp: nothing at or below the barrier top; a sub-biome
    /// at most at the echo band's highest bottom; expanded echo cells need the bare cover under
    /// their local column top, so none can reach above the cover under this chunk column's tallest top.
    /// </summary>
    internal static StampBand WriteBand(ColumnData column)
    {
        var max = SubBandTopY;
        max = Math.Max(max, column.MaxHeight - WorldGenerationConfig.CaveEchoCoverBlocks);
        return new StampBand(WorldGenerationConfig.BarrierTopY + 1, max);
    }

    /// <summary>
    /// The self-contained palette pass: classifies the chunk at world origin (worldX0, worldY0, worldZ0)
    /// into a fresh map with <see cref="Fill"/> and stamps from it. Generation fills one shared map per
    /// chunk instead and calls <see cref="Stamp(ushort[], CaveBiomeMap, int, int, int)"/>.
    /// </summary>
    public static void Stamp(ushort[] blocks, ColumnData column, int worldX0, int worldY0, int worldZ0)
    {
        var map = new CaveBiomeMap();
        if (Fill(map, column, worldX0, worldY0, worldZ0)) Stamp(blocks, map, worldX0, worldY0, worldZ0);
    }

    /// <summary>
    /// Rewrites every stone cell of the chunk at world origin (worldX0, worldY0, worldZ0) whose cave
    /// biome in <paramref name="caves"/> is not plain with <see cref="BlockFor"/> of its biome and role.
    /// Only plain stone is a host, so ores, pockets, geodes, fluids, plants and every block that needs
    /// support are left alone, and every palette block occludes like the stone it replaces, so the roles
    /// can be read in place.
    /// </summary>
    public static void Stamp(ushort[] blocks, CaveBiomeMap caves, int worldX0, int worldY0, int worldZ0)
    {
        var cells = caves.Cells;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        for (var y = 0; y < Chunk.Size; y++)
        {
            var idx = Chunk.Idx(x, y, z);
            var biome = (CaveBiome)cells[idx];
            if (biome == CaveBiome.Plain || blocks[idx] != RockBlock) continue;

            var block = BlockFor(biome, RoleAt(blocks, x, y, z), worldX0 + x, worldY0 + y, worldZ0 + z);
            if (block != blocks[idx]) blocks[idx] = block;
        }
    }

    /// <summary>
    /// Classifies every cell of the chunk at world origin (worldX0, worldY0, worldZ0) into
    /// <paramref name="map"/>, exactly as <see cref="At"/> does, whatever the cell holds (the carvers
    /// have not run yet). Returns whether some cell is not plain; when it returns false the map's cells
    /// may be stale and must not be read. Most cells are classified by their lattice cell's
    /// <see cref="CellRule"/> alone, which is exact, and only a cell near a band line or a selector
    /// threshold pays its own blend. A lattice cell wholly in the sub band (under every echo line) is
    /// classified a row at a time by <see cref="FillSubBandCell"/> (CB-5), which cut the deep band's cost.
    /// </summary>
    internal static bool Fill(CaveBiomeMap map, ColumnData column, int worldX0, int worldY0, int worldZ0)
    {
        if (worldY0 + Chunk.SizeMask <= WorldGenerationConfig.BarrierTopY) return false;

        map.Clear();

        // NaN marks a lattice point not sampled yet; the noise never returns NaN
        Span<float> echo = stackalloc float[GridCount];
        Span<float> deep = stackalloc float[GridCount];
        Span<float> selector = stackalloc float[GridCount];
        echo.Fill(float.NaN);
        deep.Fill(float.NaN);
        selector.Fill(float.NaN);

        // Per lattice cell, the rule its corner ranges decide (RuleOf), built the first time it is asked
        // for. Most cells are decided by it alone, without a blend of their own
        Span<CellRule> rules = stackalloc CellRule[CellCount];
        Span<bool> ruled = stackalloc bool[CellCount];
        Span<float> corners = stackalloc float[CellCount * CornersPerCell];

        for (var cx = 0; cx < Cells; cx++)
        for (var cy = 0; cy < Cells; cy++)
        for (var cz = 0; cz < Cells; cz++)
        {
            int x0 = cx * Stride, y0 = cy * Stride, z0 = cz * Stride;
            var wyMin = worldY0 + y0;
            var wyMax = wyMin + Mask;

            // A whole lattice cell under the barrier top is plain, with no lattice read (the per-cell fast path)
            if (wyMax <= WorldGenerationConfig.BarrierTopY)
            {
                FillLatticeCell(map, x0, y0, z0, CaveBiome.Plain);
                continue;
            }

            // A lattice cell above the barrier and at or below the lowest echo line is under the echo band in
            // every column whatever its depth and echo, so a whole row of it (one x and y, its z run) is
            // decided by its y alone (FillSubBandCell)
            var cell = (cx * Cells + cy) * Cells + cz;
            if (wyMin > WorldGenerationConfig.BarrierTopY && wyMax <= SubBandTopY)
            {
                ref readonly var sub = ref RuleOf(rules, ruled, corners, echo, deep, selector,
                    worldX0, worldY0, worldZ0, cell, x0, y0, z0);
                if (wyMax <= sub.EchoLo)
                {
                    FillSubBandCell(map, sub, corners.Slice(cell * CornersPerCell, CornersPerCell), x0, y0, z0,
                        worldY0, column);
                    continue;
                }
            }

            for (var x = x0; x < x0 + Stride; x++)
            for (var z = z0; z < z0 + Stride; z++)
            {
                var i = (x << Chunk.SizeShift) | z;
                var surface = column.Biomes[i];
                var surfaceEcho = EchoOf(surface);
                var height = column.Heights[i];
                for (var y = y0; y < y0 + Stride; y++)
                {
                    var idx = Chunk.Idx(x, y, z);
                    var wy = worldY0 + y;
                    var depth = height - wy;

                    // Exact fast paths to plain, no lattice read: at or below the barrier top Classify gives
                    // plain; above SubBandTopY a cell is above every echo line (the line at its highest clamped
                    // drift is SubBandTopY), so Classify gives plain when the column has no echo or the cell is
                    // shallower than the bare cover (CoverDepth is never below CaveEchoCoverBlocks)
                    if (wy <= WorldGenerationConfig.BarrierTopY
                        || (wy > SubBandTopY
                            && (surfaceEcho == CaveBiome.Plain || depth < WorldGenerationConfig.CaveEchoCoverBlocks)))
                    {
                        map.Set(idx, CaveBiome.Plain);
                        continue;
                    }

                    ref readonly var rule = ref RuleOf(rules, ruled, corners, echo, deep, selector,
                        worldX0, worldY0, worldZ0, cell, x, y, z);
                    if (!rule.TryDecide(wy, depth, surfaceEcho, out var biome))
                        biome = ClassifyCell(corners.Slice(cell * CornersPerCell, CornersPerCell), x, y, z, wy, depth,
                            surface);
                    map.Set(idx, biome);
                }
            }
        }

        return CaveEchoVolumes.Expand(map, column, worldX0, worldY0, worldZ0);
    }

    /// <summary>
    /// Classifies the lattice cell whose lowest chunk-local cell is (x0, y0, z0), lying above the barrier
    /// and wholly at or below its rule's lowest echo line, row by row (one x and y, the z run): exactly
    /// what <see cref="Classify"/> gives each cell. No cell is in the echo band (the exact echo line lies
    /// at or above the lowest one), so a row at or below the lowest deep line is magma deep, a row above
    /// the highest deep line is the selector's (decided for the whole cell, or from each cell's own
    /// selector blend, clamped and compared as <see cref="Classify"/> does), and only a row between the
    /// two deep lines pays the full <see cref="ClassifyCell"/>.
    /// </summary>
    private static void FillSubBandCell(CaveBiomeMap map, in CellRule rule, ReadOnlySpan<float> cellCorners,
        int x0, int y0, int z0, int worldY0, ColumnData column)
    {
        var selectorCorners = cellCorners.Slice(16, 8);
        for (var x = x0; x < x0 + Stride; x++)
        for (var y = y0; y < y0 + Stride; y++)
        {
            var wy = worldY0 + y;
            var row = Chunk.Idx(x, y, z0);
            if (wy <= rule.DeepLo)
            {
                map.SetRun(row, Stride, CaveBiome.MagmaDeep);
                continue;
            }

            if (wy > rule.DeepHi && rule.SelectorDecided)
            {
                map.SetRun(row, Stride, rule.Selected);
                continue;
            }

            var fx = ((x & Mask) + 0.5f) * InvStride;
            var fy = ((y & Mask) + 0.5f) * InvStride;
            for (var z = z0; z < z0 + Stride; z++)
            {
                CaveBiome biome;
                if (wy > rule.DeepHi)
                {
                    var fz = ((z & Mask) + 0.5f) * InvStride;
                    biome = SelectorBiome(Math.Clamp(Blend(selectorCorners, fx, fy, fz), -1f, 1f));
                }
                else
                {
                    var i = (x << Chunk.SizeShift) | z;
                    biome = ClassifyCell(cellCorners, x, y, z, wy, column.Heights[i] - wy, column.Biomes[i]);
                }

                map.Set(Chunk.Idx(x, y, z), biome);
            }
        }
    }

    /// <summary>The sub-biome a clamped selector picks under the echo band and above the deep line (<see cref="Classify"/>'s tail).</summary>
    private static CaveBiome SelectorBiome(float selector)
    {
        if (selector > WorldGenerationConfig.CaveCrystalMinValue) return CaveBiome.Crystal;
        return selector < WorldGenerationConfig.CaveFungalMaxValue ? CaveBiome.Fungal : CaveBiome.Plain;
    }

    /// <summary>
    /// Writes <paramref name="biome"/> into every cell of the lattice cell whose lowest chunk-local cell is
    /// (x0, y0, z0): what the per-cell loop writes when every cell of it takes the same decided path.
    /// </summary>
    private static void FillLatticeCell(CaveBiomeMap map, int x0, int y0, int z0, CaveBiome biome)
    {
        for (var x = x0; x < x0 + Stride; x++)
        for (var y = y0; y < y0 + Stride; y++)
            map.SetRun(Chunk.Idx(x, y, z0), Stride, biome);
    }

    /// <summary>
    /// The <see cref="CellRule"/> of the lattice cell holding chunk-local cell (x, y, z), from each
    /// noise's range over its eight corners, built the first time the cell is asked for. Its corners go
    /// to <paramref name="corners"/> (<see cref="CornersPerCell"/> a cell: per noise, the eight in
    /// <see cref="OreNoiseLattice.Trilinear"/>'s argument order) for <see cref="ClassifyCell"/>.
    /// </summary>
    private static ref readonly CellRule RuleOf(Span<CellRule> rules, Span<bool> ruled, Span<float> corners,
        Span<float> echo, Span<float> deep, Span<float> selector, int worldX0, int worldY0, int worldZ0,
        int cell, int x, int y, int z)
    {
        if (ruled[cell]) return ref rules[cell];

        var cx = x / Stride;
        var cy = y / Stride;
        var cz = z / Stride;
        var own = corners.Slice(cell * CornersPerCell, CornersPerCell);
        Range(own[..8], echo, EchoDriftNoise, worldX0, worldY0, worldZ0, cx, cy, cz, out var driftLo, out var driftHi);
        Range(own.Slice(8, 8), deep, DeepDriftNoise, worldX0, worldY0, worldZ0, cx, cy, cz,
            out var deepLo, out var deepHi);
        Range(own.Slice(16, 8), selector, SelectorNoise, worldX0, worldY0, worldZ0, cx, cy, cz,
            out var selectorLo, out var selectorHi);
        rules[cell] = CellRule.From(driftLo, driftHi, deepLo, deepHi, selectorLo, selectorHi);
        ruled[cell] = true;
        return ref rules[cell];
    }

    /// <summary>
    /// Copies a lattice cell's eight corners into <paramref name="into"/> in
    /// <see cref="OreNoiseLattice.Trilinear"/>'s argument order (c000, c001, ..., c111, digits x, y, z)
    /// and returns their minimum and maximum, widened by <see cref="BoundSlack"/>.
    /// </summary>
    private static void Range(Span<float> into, Span<float> samples, FastNoiseLite noise, int worldX0, int worldY0,
        int worldZ0, int cx, int cy, int cz, out float lo, out float hi)
    {
        lo = float.MaxValue;
        hi = float.MinValue;
        for (var dx = 0; dx <= 1; dx++)
        for (var dy = 0; dy <= 1; dy++)
        for (var dz = 0; dz <= 1; dz++)
        {
            var v = Corner(samples, noise, worldX0, worldY0, worldZ0, cx + dx, cy + dy, cz + dz);
            into[(dx << 2) | (dy << 1) | dz] = v;
            lo = Math.Min(lo, v);
            hi = Math.Max(hi, v);
        }

        lo -= BoundSlack;
        hi += BoundSlack;
    }

    /// <summary>
    /// <see cref="Classify"/> of chunk-local cell (x, y, z) from its lattice cell's corners
    /// (<see cref="RuleOf"/>): the same corners and fraction expressions as <see cref="At"/>, since the
    /// chunk origin is a lattice multiple.
    /// </summary>
    private static CaveBiome ClassifyCell(ReadOnlySpan<float> cellCorners, int x, int y, int z, int wy, int depth,
        Biome surface)
    {
        var fx = ((x & Mask) + 0.5f) * InvStride;
        var fy = ((y & Mask) + 0.5f) * InvStride;
        var fz = ((z & Mask) + 0.5f) * InvStride;
        return Classify(wy, depth, surface,
            Blend(cellCorners[..8], fx, fy, fz),
            Blend(cellCorners.Slice(8, 8), fx, fy, fz),
            Blend(cellCorners.Slice(16, 8), fx, fy, fz));
    }

    /// <summary>The trilinear blend of <paramref name="noise"/> over the lattice cell based at (bx, by, bz).</summary>
    private static float Blend(FastNoiseLite noise, int bx, int by, int bz, float fx, float fy, float fz) =>
        OreNoiseLattice.Trilinear(
            noise.GetNoise(bx, by, bz), noise.GetNoise(bx, by, bz + Stride),
            noise.GetNoise(bx, by + Stride, bz), noise.GetNoise(bx, by + Stride, bz + Stride),
            noise.GetNoise(bx + Stride, by, bz), noise.GetNoise(bx + Stride, by, bz + Stride),
            noise.GetNoise(bx + Stride, by + Stride, bz), noise.GetNoise(bx + Stride, by + Stride, bz + Stride),
            fx, fy, fz);

    /// <summary>The trilinear blend of eight corners in <see cref="OreNoiseLattice.Trilinear"/>'s argument order.</summary>
    private static float Blend(ReadOnlySpan<float> c, float fx, float fy, float fz) =>
        OreNoiseLattice.Trilinear(c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7], fx, fy, fz);

    /// <summary>
    /// The noise at lattice point (gx, gy, gz) of the chunk at world origin (worldX0, worldY0, worldZ0),
    /// sampled into <paramref name="samples"/> the first time it is asked for.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Corner(Span<float> samples, FastNoiseLite noise, int worldX0, int worldY0, int worldZ0,
        int gx, int gy, int gz)
    {
        ref var slot = ref samples[gx * GridSq + gy * Grid + gz];
        if (float.IsNaN(slot))
            slot = noise.GetNoise(worldX0 + gx * Stride, worldY0 + gy * Stride, worldZ0 + gz * Stride);
        return slot;
    }


}
