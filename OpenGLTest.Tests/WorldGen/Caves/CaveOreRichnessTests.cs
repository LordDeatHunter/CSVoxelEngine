using System;
using System.Collections.Generic;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Caves;
using VoxelEngine.WorldGen.Features.Underground;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Caves;

/// <summary>
/// The cave biome richness of the four Y-band ores (plans/CAVE_BIOMES.md CB-2): a per-cell threshold shift on
/// <see cref="OreNoiseLattice"/>. A plain map stamps exactly the plain field; the stamp equals the point query
/// for every biome, and for a mixed map with each cell's own biome; and a biome's ore set grows, shrinks or
/// stays as the sign of its shift says. In the "Worldgen seed" collection because it reads the worldgen noises.
/// </summary>
[Collection("Worldgen seed")]
public sealed class CaveOreRichnessTests
{
    private sealed record Ore(
        string Name,
        Action<ushort[], int, int, int, CaveBiomeMap?> Stamp,
        Func<int, int, int, CaveBiome, bool> HasOre,
        CaveRichness Richness,
        ushort Block,
        int[] ChunkYs,
        bool Sandstone);

    private static readonly int[] ChunkXz = { -1, 0 };

    public CaveOreRichnessTests() => TestContent.EnsureLoaded();

    private static IEnumerable<Ore> Ores()
    {
        const int hematiteTop = WorldGenerationConfig.HematiteMaxY >> Chunk.SizeShift;
        yield return new Ore("malachite", MalachiteGenerator.Stamp, MalachiteGenerator.HasOre,
            MalachiteGenerator.Richness, MalachiteGenerator.OreBlock, new[] { -3, -2 }, false);
        yield return new Ore("coal", CoalGenerator.Stamp, CoalGenerator.HasOre,
            CoalGenerator.Richness, CoalGenerator.OreBlock, new[] { -3, -2 }, true);
        yield return new Ore("hematite", HematiteGenerator.Stamp, HematiteGenerator.HasOre,
            HematiteGenerator.Richness, HematiteGenerator.OreBlock, new[] { hematiteTop - 1, hematiteTop }, false);
        yield return new Ore("tin", TinGenerator.Stamp, TinGenerator.HasOre,
            TinGenerator.Richness, TinGenerator.OreBlock,
            new[] { WorldGenerationConfig.TinMinY >> Chunk.SizeShift, WorldGenerationConfig.TinMaxY >> Chunk.SizeShift },
            false);
    }

    private static IEnumerable<(int X0, int Y0, int Z0)> Chunks(Ore ore)
    {
        foreach (var cx in ChunkXz)
        foreach (var cy in ore.ChunkYs)
        foreach (var cz in ChunkXz)
            yield return (cx * Chunk.Size, cy * Chunk.Size, cz * Chunk.Size);
    }

    /// <summary>Stone, with about a third of the cells sandstone when the ore hosts on it too.</summary>
    private static void FillHosts(ushort[] blocks, Ore ore, int x0, int y0, int z0)
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var sandstone = ContentRegistry.RequireBlock("sandstone").Id;
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var h = SplitMix64.Mix((ulong)(uint)(x0 + x) * 0x9E3779B97F4A7C15UL
                                   ^ (ulong)(uint)(y0 + y) * 0xC2B2AE3D27D4EB4FUL
                                   ^ (ulong)(uint)(z0 + z) * 0x165667B19E3779F9UL);
            blocks[Chunk.Idx(x, y, z)] = ore.Sandstone && h % 3 == 0 ? sandstone : stone;
        }
    }

    private static CaveBiomeMap MapOf(Func<int, CaveBiome> biomeOf)
    {
        var map = new CaveBiomeMap();
        for (var i = 0; i < map.Cells.Length; i++) map.Cells[i] = (byte)biomeOf(i);
        map.Recount();
        return map;
    }

    private static ushort[] Stamped(Ore ore, CaveBiomeMap? map, int x0, int y0, int z0)
    {
        var blocks = new ushort[Chunk.BlockCount];
        FillHosts(blocks, ore, x0, y0, z0);
        ore.Stamp(blocks, x0, y0, z0, map);
        return blocks;
    }

    [Fact]
    public void APlainMapStampsExactlyThePlainField()
    {
        var plain = MapOf(_ => CaveBiome.Plain);
        foreach (var ore in Ores())
        foreach (var (x0, y0, z0) in Chunks(ore))
            Assert.True(Stamped(ore, plain, x0, y0, z0).AsSpan().SequenceEqual(Stamped(ore, null, x0, y0, z0)),
                $"{ore.Name}: a plain map changed the stamp of chunk ({x0}, {y0}, {z0})");
    }

    [Fact]
    public void TheStampEqualsThePointQueryInEveryBiome()
    {
        foreach (var ore in Ores())
        {
            var oreCells = 0;
            foreach (var biome in Enum.GetValues<CaveBiome>())
            {
                var map = MapOf(_ => biome);
                // Two chunks a biome keep the point queries affordable; the seams are covered by OreNoiseLatticeTests
                var (x0, y0, z0) = (ChunkXz[0] * Chunk.Size, ore.ChunkYs[^1] * Chunk.Size, ChunkXz[1] * Chunk.Size);
                oreCells += AssertMatchesPointQuery(ore, map, x0, y0, z0);
            }

            Assert.True(oreCells > 0, $"no {ore.Name} at all, so the comparison proves nothing");
        }
    }

    [Fact]
    public void AMixedMapMatchesThePointQueryWithEachCellsOwnBiome()
    {
        foreach (var ore in Ores())
        {
            // The biome whose shift lies farthest from 0, so the mix is a real per-cell difference
            var other = CaveBiome.Plain;
            foreach (var biome in Enum.GetValues<CaveBiome>())
                if (MathF.Abs(ore.Richness.ShiftOf(biome)) > MathF.Abs(ore.Richness.ShiftOf(other))) other = biome;
            Assert.True(other != CaveBiome.Plain, $"{ore.Name} has no richness to mix");

            var map = MapOf(i => SplitMix64.Mix((ulong)i * 0x9E3779B97F4A7C15UL) % 2 == 0 ? other : CaveBiome.Plain);
            var oreCells = 0;
            foreach (var (x0, y0, z0) in Chunks(ore))
                oreCells += AssertMatchesPointQuery(ore, map, x0, y0, z0);
            Assert.True(oreCells > 0, $"no {ore.Name} in the mixed map, so the comparison proves nothing");
        }
    }

    /// <summary>Asserts every cell of the stamp equals the point query with that cell's map biome; returns the ore count.</summary>
    private static int AssertMatchesPointQuery(Ore ore, CaveBiomeMap map, int x0, int y0, int z0)
    {
        var input = new ushort[Chunk.BlockCount];
        FillHosts(input, ore, x0, y0, z0);
        var output = Stamped(ore, map, x0, y0, z0);
        var oreCells = 0;
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var idx = Chunk.Idx(x, y, z);
            var biome = (CaveBiome)map.Cells[idx];
            var expected = ore.HasOre(x0 + x, y0 + y, z0 + z, biome) ? ore.Block : input[idx];
            if (output[idx] == ore.Block) oreCells++;
            if (output[idx] != expected)
                Assert.Fail($"{ore.Name}: the stamp and the point query disagree at ({x0 + x}, {y0 + y}, {z0 + z}) in {biome}");
        }

        return oreCells;
    }

    [Fact]
    public void ABiomesOreSetFollowsTheSignOfItsShift()
    {
        var plainMap = MapOf(_ => CaveBiome.Plain);
        foreach (var ore in Ores())
        foreach (var biome in Enum.GetValues<CaveBiome>())
        {
            var shift = ore.Richness.ShiftOf(biome);
            var map = MapOf(_ => biome);
            var differs = false;
            foreach (var (x0, y0, z0) in Chunks(ore))
            {
                var plain = Stamped(ore, plainMap, x0, y0, z0);
                var shifted = Stamped(ore, map, x0, y0, z0);
                for (var i = 0; i < plain.Length; i++)
                {
                    bool inPlain = plain[i] == ore.Block, inShifted = shifted[i] == ore.Block;
                    if (inPlain == inShifted) continue;

                    differs = true;
                    var at = $"{ore.Name} in {biome} (shift {shift})";
                    if (shift == 0f) Assert.Fail($"{at}: a zero shift changed the ore set");
                    if (shift < 0f) Assert.True(inShifted, $"{at}: a richer biome lost an ore the plain field has");
                    else Assert.True(inPlain, $"{at}: a poorer biome gained an ore the plain field lacks");
                }
            }

            Assert.True(shift == 0f || differs, $"{ore.Name} in {biome}: the shift {shift} never changed a cell");
        }
    }
}
