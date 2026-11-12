using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Interaction;
using VoxelEngine.Items;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Caves;
using VoxelEngine.WorldGen.Features.Underground;
using Xunit;
using Stage = VoxelEngine.WorldGen.WorldgenStageTimer.Stage;

namespace VoxelEngine.Tests.WorldGen.Caves;

/// <summary>
/// The cave biomes (<see cref="CaveBiomes"/>, plans/CAVE_BIOMES.md CB-1): the palette pass equals the point
/// query cell for cell across every chunk seam and touches only plain stone; the bands keep their order
/// and never reach the barrier or hell; the band edges wander instead of lying flat; the field is a pure
/// function of the seed; and every palette block is a safe, ordinarily mined stand-in for stone.
/// In the "Worldgen seed" collection because one fact reseeds.
/// </summary>
[Collection("Worldgen seed")]
public sealed class CaveBiomeTests
{
    private const int ColumnRange = 1250;

    private static readonly Lazy<(int Cx, int Cz)> FrozenColumn = new(() => FindColumn(Biome.Frozen, 0xC4FE_0001UL));
    private static readonly Lazy<(int Cx, int Cz)> TaigaColumn = new(() => FindColumn(Biome.Taiga, 0xC4FE_0003UL));
    private static readonly Lazy<(int Cx, int Cz)> DesertColumn = new(() => FindColumn(Biome.Desert, 0xC4FE_0002UL));

    public CaveBiomeTests() => TestContent.EnsureLoaded();

    /// <summary>A chunk column whose center column's stored biome is <paramref name="biome"/>, by bounded draws.</summary>
    private static (int Cx, int Cz) FindColumn(Biome biome, ulong seed)
    {
        var state = seed;
        for (var draw = 0; draw < 20000; draw++)
        {
            var cx = (int)(Next(ref state) % (2 * ColumnRange)) - ColumnRange;
            var cz = (int)(Next(ref state) % (2 * ColumnRange)) - ColumnRange;
            // The cheap point query first, so only likely candidates pay a column
            if (TerrainGenerator.BiomeAt(cx * Chunk.Size + 8, cz * Chunk.Size + 8) != biome) continue;
            if (ColumnCache.Get(cx, cz).Biomes[(8 << Chunk.SizeShift) | 8] == biome) return (cx, cz);
        }

        throw new InvalidOperationException($"no {biome} chunk column found");
    }

    private static ulong Next(ref ulong state) => SplitMix64.Mix(state += 0x9E3779B97F4A7C15UL);

    /// <summary>The chunk y two chunks under the lowest ground of chunk column (cx, cz): inside its echo band.</summary>
    private static int EchoChunkY(int cx, int cz) => (ColumnCache.Get(cx, cz).MinHeight >> Chunk.SizeShift) - 2;

    [Fact]
    public void TaigaAndFrozenShareTheUndergroundPolicyIncludingCoverAndRealmGates()
    {
        foreach (var y in new[] { 40, -60, -180, -240, WorldGenerationConfig.BarrierTopY, WorldGenerationConfig.BarrierTopY - 20 })
        foreach (var depth in new[] { 0, 4, 9, 20, 100, 400 })
        foreach (var drift in new[] { -1f, 0f, 1f })
            Assert.Equal(CaveBiomes.Classify(y, depth, Biome.Frozen, drift, drift, drift),
                CaveBiomes.Classify(y, depth, Biome.Taiga, drift, drift, drift));
    }

    [Fact]
    public void TheStampMatchesThePointQueryAcrossEverySeam()
    {
        var (fx, fz) = FrozenColumn.Value;
        var (dx, dz) = DesertColumn.Value;
        var (tx, tz) = TaigaColumn.Value;
        // Each block: the 2x2 chunk columns ending at (cx, cz) and the two chunk ys ending at cy. An echo
        // block and a block reaching under the deep's lowest top always hold biome rock, so they must change
        var blocks = new List<(int Cx, int Cz, int Cy, bool MustChange)>
        {
            (fx, fz, EchoChunkY(fx, fz), true),
            (dx, dz, EchoChunkY(dx, dz), true),
            (tx, tz, EchoChunkY(tx, tz), true),
            (tx, tz, -17, false),
            (dx, dz, -14, false),
            (fx, fz, -17, true),
            (-2, -4, -14, false),
            (-2, -4, -17, true),
        };

        var stone = ContentRegistry.RequireBlock("stone").Id;
        var others = new[] { TinGenerator.OreBlock, UndergroundPoolGenerator.WaterBlock, GlowMushroomGenerator.MushroomBlock };
        var input = new ushort[Chunk.Size * Chunk.Size * Chunk.Size];
        var output = new ushort[input.Length];
        var changed = 0;
        foreach (var (bcx, bcz, bcy, mustChange) in blocks)
        {
            var blockChanged = 0;
            for (var cx = bcx - 1; cx <= bcx; cx++)
            for (var cz = bcz - 1; cz <= bcz; cz++)
            for (var cy = bcy - 1; cy <= bcy; cy++)
            {
                var column = ColumnCache.Get(cx, cz);
                int x0 = cx * Chunk.Size, y0 = cy * Chunk.Size, z0 = cz * Chunk.Size;
                FillSynthetic(input, stone, others, x0, y0, z0);
                Array.Copy(input, output, input.Length);
                CaveBiomes.Stamp(output, column, x0, y0, z0);

                for (var x = 0; x < Chunk.Size; x++)
                for (var y = 0; y < Chunk.Size; y++)
                for (var z = 0; z < Chunk.Size; z++)
                {
                    var idx = Chunk.Idx(x, y, z);
                    if (output[idx] != input[idx]) blockChanged++;
                    if (input[idx] != stone)
                    {
                        Assert.True(output[idx] == input[idx], $"a non-stone cell changed at ({x0 + x}, {y0 + y}, {z0 + z})");
                        continue;
                    }

                    var i = (x << Chunk.SizeShift) | z;
                    var biome = CaveBiomes.At(x0 + x, y0 + y, z0 + z, column.Biomes[i], column.Heights[i]);
                    var expected = CaveBiomes.BlockFor(biome, CaveBiomes.RoleAt(input, x, y, z), x0 + x, y0 + y, z0 + z);
                    if (output[idx] == expected) continue;
                    Assert.Fail($"the stamp and the point query disagree at ({x0 + x}, {y0 + y}, {z0 + z}): {biome}");
                }
            }

            Assert.True(!mustChange || blockChanged > 0, $"nothing changed around chunk ({bcx}, {bcy}, {bcz}), so it proves nothing");
            changed += blockChanged;
        }

        Assert.True(changed > 0);
    }

    [Fact]
    public void TheMapMatchesThePointQueryAcrossEverySeam()
    {
        var (fx, fz) = FrozenColumn.Value;
        var (dx, dz) = DesertColumn.Value;
        var (tx, tz) = TaigaColumn.Value;
        var blocks = new (int Cx, int Cz, int Cy)[]
        {
            (fx, fz, EchoChunkY(fx, fz)),
            (fx, fz, EchoChunkY(fx, fz) + 3),
            (dx, dz, EchoChunkY(dx, dz)),
            (tx, tz, EchoChunkY(tx, tz)),
            (tx, tz, -17),
            (dx, dz, -14),
            (fx, fz, -17),
            (-2, -4, -14),
            (-2, -4, -17),
            (-2, -4, 2),
            (5, 7, (WorldGenerationConfig.BarrierTopY >> Chunk.SizeShift) + 1),
            // More of the sub band and the deep line, where Fill decides whole lattice cells at once
            (12, -9, -13),
            (-21, 17, -15),
            (30, 30, -16),
        };

        var map = new CaveBiomeMap();
        int filled = 0, empty = 0;
        foreach (var (bcx, bcz, bcy) in blocks)
        for (var cx = bcx - 1; cx <= bcx; cx++)
        for (var cz = bcz - 1; cz <= bcz; cz++)
        for (var cy = bcy - 1; cy <= bcy; cy++)
        {
            var column = ColumnCache.Get(cx, cz);
            int x0 = cx * Chunk.Size, y0 = cy * Chunk.Size, z0 = cz * Chunk.Size;
            var any = CaveBiomes.Fill(map, column, x0, y0, z0);
            var anyExpected = false;
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var i = (x << Chunk.SizeShift) | z;
                var expected = CaveBiomes.At(x0 + x, y0 + y, z0 + z, column.Biomes[i], column.Heights[i]);
                anyExpected |= expected != CaveBiome.Plain;
                if (!any) continue;

                var actual = (CaveBiome)map.Cells[Chunk.Idx(x, y, z)];
                if (actual != expected)
                    Assert.Fail($"the map and the point query disagree at ({x0 + x}, {y0 + y}, {z0 + z}): {actual} vs {expected}");
                Assert.True(map.Has(actual));
            }

            Assert.True(any == anyExpected, $"Fill reported {any} for chunk ({cx}, {cy}, {cz}), the point query {anyExpected}");
            if (any) filled++;
            else empty++;
        }

        Assert.True(filled > 0 && empty > 0, "the sample never held both a plain and a non-plain chunk");
    }

    /// <summary>All stone, about a quarter opened to air and a few other blocks, by a hash of the world cell.</summary>
    private static void FillSynthetic(ushort[] blocks, ushort stone, ushort[] others, int x0, int y0, int z0)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var h = SplitMix64.Mix((ulong)(uint)(x0 + x) * 0x9E3779B97F4A7C15UL
                                   ^ (ulong)(uint)(y0 + y) * 0xC2B2AE3D27D4EB4FUL
                                   ^ (ulong)(uint)(z0 + z) * 0x165667B19E3779F9UL);
            var roll = (int)(h % 100);
            blocks[Chunk.Idx(x, y, z)] = roll switch
            {
                < 25 => BlockType.Air,
                < 28 => others[(int)((h >> 32) % (ulong)others.Length)],
                _ => stone,
            };
        }
    }

    [Theory]
    [InlineData(Biome.Frozen, CaveBiome.FrozenEcho)]
    [InlineData(Biome.Taiga, CaveBiome.FrozenEcho)]
    [InlineData(Biome.Desert, CaveBiome.DesertEcho)]
    public void ExpandedEchoDressesUncarvedRockBelowTheOldBand(Biome surface, CaveBiome expected)
    {
        var (cx, cz) = FindColumn(surface, 0xC4FE_0100UL + (ulong)surface);
        var column = ColumnCache.Get(cx, cz);
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var changed = 0;
        for (var cy = -23; cy <= -15; cy++)
        {
            var map = new CaveBiomeMap();
            CaveBiomes.Fill(map, column, cx * Chunk.Size, cy * Chunk.Size, cz * Chunk.Size);
            var blocks = Enumerable.Repeat(stone, Chunk.BlockCount).ToArray();
            CaveBiomes.Stamp(blocks, map, cx * Chunk.Size, cy * Chunk.Size, cz * Chunk.Size);
            for (var i = 0; i < blocks.Length; i++)
            {
                Assert.NotEqual(BlockType.Air, blocks[i]);
                if ((CaveBiome)map.Cells[i] != expected) continue;
                changed++;
                Assert.Equal(CaveBiomes.Palette(expected).Stone, blocks[i]);
            }
        }
        Assert.True(changed > 0, "expanded material never reached solid rock below the original band");
    }

    [Fact]
    public void BandsAreBoundedAndOrdered()
    {
        var biomes = Enum.GetValues<Biome>();
        var seen = new HashSet<CaveBiome>();
        var state = 0xC4FE_0003UL;
        for (var n = 0; n < 20000; n++)
        {
            var x = (int)(Next(ref state) % 20000) - 10000;
            var z = (int)(Next(ref state) % 20000) - 10000;
            var yMin = WorldGenerationConfig.HellTopY - 64;
            var y = yMin + (int)(Next(ref state) % (ulong)(160 - yMin + 1));
            var surface = biomes[Next(ref state) % (ulong)biomes.Length];
            var height = y - 40 + (int)(Next(ref state) % 341);
            var cave = CaveBiomes.At(x, y, z, surface, height);
            seen.Add(cave);
            var at = $"({x}, {y}, {z}) under {surface} at {height}: {cave}";

            if (y <= WorldGenerationConfig.BarrierTopY) Assert.True(cave == CaveBiome.Plain, at);

            if (cave is CaveBiome.FrozenEcho or CaveBiome.DesertEcho)
            {
                Assert.True(y > Math.Min(WorldGenerationConfig.CaveEchoVolumeBottomY,
                    WorldGenerationConfig.CaveEchoBottomY - WorldGenerationConfig.CaveEchoDriftBlocks), at);
                Assert.True(height - y >= WorldGenerationConfig.CaveEchoCoverBlocks, at);
                // Expanded 3D echoes can extend under a different surface biome.
            }

            if (cave is CaveBiome.Crystal or CaveBiome.Fungal or CaveBiome.MagmaDeep)
                Assert.True(y <= WorldGenerationConfig.CaveEchoBottomY + WorldGenerationConfig.CaveEchoDriftBlocks, at);

            if (cave == CaveBiome.MagmaDeep)
                Assert.True(y <= WorldGenerationConfig.CaveDeepTopY + WorldGenerationConfig.CaveDeepDriftBlocks, at);
        }

        foreach (var cave in Enum.GetValues<CaveBiome>())
            Assert.True(seen.Contains(cave), $"{cave} never occurred, so its rules were never checked");
    }

    [Fact]
    public void TheTransitionsAreNotFlatLines()
    {
        const int height = 80;
        var echoBottoms = new HashSet<int>();
        var deepTops = new HashSet<int>();
        var state = 0xC4FE_0004UL;
        for (var n = 0; n < 48; n++)
        {
            var x = (int)(Next(ref state) % 8000) - 4000;
            var z = (int)(Next(ref state) % 8000) - 4000;

            int? echoBottom = null;
            int? deepTop = null;
            var wasEcho = false;
            for (var y = height; y > WorldGenerationConfig.BarrierTopY; y--)
            {
                var cave = CaveBiomes.At(x, y, z, Biome.Frozen, height);
                if (cave is CaveBiome.FrozenEcho or CaveBiome.DesertEcho) wasEcho = true;
                else if (wasEcho && echoBottom is null) echoBottom = y;
                if (cave == CaveBiome.MagmaDeep)
                {
                    deepTop = y;
                    break;
                }
            }

            Assert.NotNull(echoBottom);
            Assert.NotNull(deepTop);
            echoBottoms.Add(echoBottom.Value);
            deepTops.Add(deepTop.Value);
        }

        Assert.True(echoBottoms.Count >= 2, "the echo band ends at one flat height everywhere");
        Assert.True(deepTops.Count >= 2, "the deep starts at one flat height everywhere");
    }

    [Fact]
    public void TheFieldIsDeterministicPerSeed()
    {
        var points = new List<(int X, int Y, int Z, Biome Surface, int Height)>();
        var state = 0xC4FE_0005UL;
        for (var n = 0; n < 400; n++)
        {
            var x = (int)(Next(ref state) % 4000) - 2000;
            var z = (int)(Next(ref state) % 4000) - 2000;
            var y = WorldGenerationConfig.BarrierTopY + 1 + (int)(Next(ref state) % 260);
            var surface = (n & 1) == 0 ? Biome.Frozen : Biome.Desert;
            points.Add((x, y, z, surface, 60));
        }

        CaveBiome[] Sample() => points.Select(p => CaveBiomes.At(p.X, p.Y, p.Z, p.Surface, p.Height)).ToArray();

        var first = Sample();
        try
        {
            WorldSeed.Apply(WorldSeed.Default + 7919);
            Assert.False(first.SequenceEqual(Sample()), "another seed laid out exactly the same cave biomes");
        }
        finally
        {
            WorldSeed.Apply(WorldSeed.Default);
        }

        Assert.True(first.SequenceEqual(Sample()), "the same seed laid out different cave biomes");
    }

    [Fact]
    public void PaletteBlocksAreSafeHosts()
    {
        var paletteBlocks = new HashSet<ushort>();
        foreach (var cave in Enum.GetValues<CaveBiome>())
        {
            if (cave == CaveBiome.Plain) continue;

            var p = CaveBiomes.Palette(cave);
            paletteBlocks.Add(p.Stone);
            paletteBlocks.Add(p.Floor);
            paletteBlocks.Add(p.Ceiling);
            if (p.AccentRate > 0) paletteBlocks.Add(p.Accent);
        }

        foreach (var id in paletteBlocks)
        {
            var name = BlockType.ById[id].Name;
            Assert.True(BlockType.IsSolid(id), $"{name} is not solid");
            Assert.True(BlockType.Occludes(id), $"{name} does not occlude");
            Assert.False(BlockType.IsFluid(id), $"{name} is a fluid");
            Assert.False(BlockType.IsFalling(id), $"{name} falls");
            Assert.False(BlockType.ById[id].NeedsSupport, $"{name} needs support");
        }

        // Some pickaxe the barrier refuses mines every palette block: the cave biomes never gate like it
        var pickaxes = ItemRegistry.All.Where(item => ContentRegistry.IsInTag("pickaxe", item.Id)).ToList();
        Assert.Contains(pickaxes, pick => !Mining.CanMine(BarrierGenerator.BarrierBlock, pick)
                                          && paletteBlocks.All(id => Mining.CanMine(id, pick)));
    }

    [Fact]
    public void InRealChunksThePassChangesOnlyStone()
    {
        var (fx, fz) = FrozenColumn.Value;
        var (dx, dz) = DesertColumn.Value;
        var (tx, tz) = TaigaColumn.Value;
        var positions = new[]
        {
            new Vector3(fx, EchoChunkY(fx, fz), fz),
            new Vector3(fx, EchoChunkY(fx, fz) - 1, fz),
            new Vector3(dx, EchoChunkY(dx, dz), dz),
            new Vector3(dx, EchoChunkY(dx, dz) - 1, dz),
            new Vector3(tx, EchoChunkY(tx, tz), tz),
            new Vector3(tx, -17, tz),
            new Vector3(fx, -14, fz),
            new Vector3(dx, -15, dz),
            new Vector3(-3, -17, 5),
        };

        var stone = ContentRegistry.RequireBlock("stone").Id;
        ushort[]? before = null;
        ushort[]? after = null;
        Chunk? current = null;
        var timer = new WorldgenStageTimer
        {
            AfterMark = s =>
            {
                if (s == Stage.UndergroundPools) before = (ushort[])ChunkBlocks.DenseScratch.Clone();
                else if (s == Stage.CaveBiomes) after = (ushort[])ChunkBlocks.DenseScratch.Clone();
            },
        };

        var changed = 0;
        foreach (var pos in positions)
        {
            before = after = null;
            current = new Chunk(null!, pos, generateBlockData: false);
            ChunkTerrainFill.GenerateBlockData(current, timer);
            Assert.NotNull(before);
            Assert.NotNull(after);

            for (var i = 0; i < before.Length; i++)
            {
                if (before[i] == after[i]) continue;

                changed++;
                Assert.True(before[i] == stone, $"the pass rewrote a {BlockType.ById[before[i]].Name} cell in chunk {pos}");
                Assert.True(BlockType.IsSolid(after[i]), $"the pass wrote a non-solid cell in chunk {pos}");
            }
        }

        Assert.True(changed > 0, "the pass changed nothing in the real chunks, so the rule proves nothing");
    }
}
