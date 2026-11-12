using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features.Underground;
using VoxelEngine.WorldGen.Hell;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Features.Underground;

/// <summary>
/// Vulcanite veins: the stamp replaces basalt and nothing else and equals its point query, nothing lies above the
/// barrier band, the lava proximity peaks at the lava and fades away from it, the veins are denser near lava than away
/// from it (in hell and in the band alike), a real generated chunk holds ore only where the band or hell laid basalt,
/// and the veins follow the world seed.
/// </summary>
[Collection("Worldgen seed")]
public sealed class VulcaniteTests
{
    private const int Volume = Chunk.Size * Chunk.Size * Chunk.Size;
    private const int Period = WorldGenerationConfig.HellLayerPeriodBlocks;

    public VulcaniteTests() => TestContent.EnsureLoaded();

    private static ushort Id(string name) => ContentRegistry.RequireBlock(name).Id;

    private static ushort Basalt => VulcaniteGenerator.HostBlock;

    /// <summary>The row of hell layer <paramref name="layer"/> standing right over its lava level.</summary>
    private static int ShoreRow(int layer)
    {
        var top = WorldGenerationConfig.HellTopY - layer * Period;
        for (var y = top; y > top - Period; y--)
            if (HellGenerator.RowsAboveLava(y) == 1) return y;
        throw new InvalidOperationException($"hell layer {layer} has no row over its lava level");
    }

    /// <summary>The floor of the chunk holding world row <paramref name="y"/>.</summary>
    private static int ChunkFloor(int y) => (y >> Chunk.SizeShift) * Chunk.Size;

    [Fact]
    public void TheStampReplacesOnlyBasaltAndEqualsThePointQuery()
    {
        // A hell chunk by the lava and one in the band's core, each mostly basalt with other blocks mixed in
        ushort[] others = [Id("hellrock"), Id("lava"), Id("stone"), BlockType.Air];
        var blocks = new ushort[Volume];
        var input = new ushort[Volume];
        var oreCells = 0;
        foreach (var y0 in new[] { ChunkFloor(ShoreRow(0)), ChunkFloor(WorldGenerationConfig.BarrierFullTopY - 1) })
        for (var x0 = 0; x0 < 64; x0 += Chunk.Size)
        for (var z0 = -32; z0 < 32; z0 += Chunk.Size)
        {
            for (var i = 0; i < Volume; i++)
                input[i] = i % 5 == 0 ? others[i / 5 % others.Length] : Basalt;
            Array.Copy(input, blocks, Volume);
            VulcaniteGenerator.Stamp(blocks, x0, y0, z0);
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var i = Chunk.Idx(x, y, z);
                Assert.Equal(VulcaniteGenerator.Over(input[i], x0 + x, y0 + y, z0 + z), blocks[i]);
                if (input[i] != Basalt) Assert.Equal(input[i], blocks[i]);
                if (blocks[i] == VulcaniteGenerator.OreBlock) oreCells++;
            }
        }

        Assert.True(oreCells > 0, "no vulcanite in basalt by the lava or in the band");
    }

    [Fact]
    public void NoVulcaniteAboveTheBarrierBand()
    {
        // The chunk straddling the band's top, all basalt: only the rows at or under the top may turn
        var y0 = ChunkFloor(WorldGenerationConfig.BarrierTopY);
        var blocks = new ushort[Volume];
        for (var x0 = 0; x0 < 128; x0 += Chunk.Size)
        {
            Array.Fill(blocks, Basalt);
            VulcaniteGenerator.Stamp(blocks, x0, y0, 0);
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
                if (y0 + y > WorldGenerationConfig.BarrierTopY)
                    Assert.Equal(Basalt, blocks[Chunk.Idx(x, y, z)]);
        }

        for (var x = 0; x < 64; x++)
        for (var z = 0; z < 64; z++)
        for (var y = WorldGenerationConfig.BarrierTopY + 1; y <= WorldGenerationConfig.BarrierTopY + 16; y++)
            Assert.False(VulcaniteGenerator.HasOre(x, y, z));
    }

    [Fact]
    public void TheLavaProximityPeaksAtTheLavaAndFadesAwayFromIt()
    {
        foreach (var layer in new[] { 0, 1, 5 })
        {
            // Up the pocket from the lava level: highest right over the lava, never rising, gone well before the ceiling
            var shore = ShoreRow(layer);
            Assert.True(VulcaniteGenerator.LavaProximity(0, shore, 0) > 0f, $"layer {layer}'s shore is out of reach");
            var previous = float.PositiveInfinity;
            for (var y = shore; HellGenerator.RowsAboveLava(y) > 0; y++)
            {
                var proximity = VulcaniteGenerator.LavaProximity(0, y, 0);
                Assert.True(proximity <= previous, $"the proximity rises up the pocket at y {y}");
                previous = proximity;
            }
            Assert.Equal(0f, previous);
        }

        // In the band: full in every column that ponds lava, and never higher in a column farther from a patch
        var columns = new List<(float Patch, float Proximity)>();
        var bandY = WorldGenerationConfig.BarrierFullTopY - 1;
        for (var x = 0; x < 1024; x += 16)
        for (var z = 0; z < 1024; z += 16)
            columns.Add((LavaGenerator.PatchValue(x, z), VulcaniteGenerator.LavaProximity(x, bandY, z)));
        Assert.Contains(columns, c => c.Patch > WorldGenerationConfig.LavaPatchThreshold);
        Assert.Contains(columns, c => c.Proximity == 0f);
        Assert.All(columns.Where(c => c.Patch > WorldGenerationConfig.LavaPatchThreshold), c => Assert.Equal(1f, c.Proximity));
        var sorted = columns.OrderBy(c => c.Patch).ToList();
        for (var i = 1; i < sorted.Count; i++)
            Assert.True(sorted[i].Proximity >= sorted[i - 1].Proximity, "the band's proximity falls toward a lava patch");

        Assert.Equal(0f, VulcaniteGenerator.LavaProximity(0, WorldGenerationConfig.BarrierTopY + 1, 0));
    }

    [Fact]
    public void TheVeinsAreDenserNearLavaThanAwayFromIt()
    {
        // Hell: rows right over the lava against rows out of its reach, over the same columns
        var shore = ShoreRow(0);
        var (near, nearOre, far, farOre) = (0, 0, 0, 0);
        for (var y = shore; y < shore + 24 && HellGenerator.RowsAboveLava(y) > 0; y++)
        for (var x = 0; x < 128; x++)
        for (var z = 0; z < 128; z++)
            Tally(x, y, z, ref near, ref nearOre, ref far, ref farOre);
        AssertDenserNear("hell", near, nearOre, far, farOre);

        // The band's core: columns in and by the deep lava patches against columns far from any
        (near, nearOre, far, farOre) = (0, 0, 0, 0);
        for (var y = WorldGenerationConfig.BarrierFullTopY - 4; y <= WorldGenerationConfig.BarrierFullTopY; y++)
        for (var x = 0; x < 768; x += 3)
        for (var z = 0; z < 768; z += 3)
            Tally(x, y, z, ref near, ref nearOre, ref far, ref farOre);
        AssertDenserNear("the band", near, nearOre, far, farOre);
    }

    /// <summary>Counts cell (x, y, z) as near lava (proximity 3/4 or more) or out of its reach (0), and its ore.</summary>
    private static void Tally(int x, int y, int z, ref int near, ref int nearOre, ref int far, ref int farOre)
    {
        var proximity = VulcaniteGenerator.LavaProximity(x, y, z);
        if (proximity >= 0.75f)
        {
            near++;
            if (VulcaniteGenerator.HasOre(x, y, z)) nearOre++;
        }
        else if (proximity == 0f)
        {
            far++;
            if (VulcaniteGenerator.HasOre(x, y, z)) farOre++;
        }
    }

    private static void AssertDenserNear(string where, int near, int nearOre, int far, int farOre)
    {
        Assert.True(near > 0 && far > 0, $"{where}: the sample holds no near ({near}) or no far ({far}) cells");
        Assert.True(nearOre > 0, $"{where}: no vulcanite near the lava");
        Assert.True((double)nearOre / near > (double)farOre / far,
            $"{where}: near {nearOre}/{near} is not denser than far {farOre}/{far}");
    }

    [Fact]
    public void EveryHellBiomeWearingBasaltPondsLavaInEveryPool()
    {
        // The hell proximity reads the layer's lava level as lava's surface: true only while basalt lines all-lava biomes
        foreach (var biome in Enum.GetValues<HellBiome>())
        {
            var p = HellBiomes.Palette(biome);
            ushort[] skin = [p.Surface, p.Soil, p.Wall, p.Ceiling, p.PoolBed, p.CeilingLight, p.FloorLight];
            if (skin.Contains(Basalt))
                Assert.True(p.LavaShare >= 1f, $"{biome} wears basalt over dry pools");
        }
    }

    [Fact]
    public void InRealChunksVulcaniteSitsOnlyWhereTheBandOrHellLaidBasalt()
    {
        // A Magma Caverns column of hell's top layer, its chunk by the lava, and a chunk of the band's core
        var magma = Enumerable.Range(0, 4096).Select(i => (Cx: i % 64, Cz: i / 64))
            .First(c => HellBiomes.AtLayer(c.Cx * Chunk.Size, 0, c.Cz * Chunk.Size) == HellBiome.MagmaCaverns
                        && HellBiomes.AtLayer(c.Cx * Chunk.Size + Chunk.Size - 1, 0, c.Cz * Chunk.Size + Chunk.Size - 1) == HellBiome.MagmaCaverns);
        (int Cx, int Cy, int Cz)[] chunks =
        [
            (magma.Cx, ShoreRow(0) >> Chunk.SizeShift, magma.Cz),
            (magma.Cx, (ShoreRow(0) >> Chunk.SizeShift) + 1, magma.Cz),
            (3, (WorldGenerationConfig.BarrierFullTopY - 1) >> Chunk.SizeShift, -2),
            (0, (WorldGenerationConfig.BarrierFullTopY + 10) >> Chunk.SizeShift, 1),
        ];

        var basaltOrOre = 0;
        foreach (var (cx, cy, cz) in chunks)
        {
            var chunk = new Chunk(null!, new Vector3(cx, cy, cz), generateBlockData: false);
            ChunkTerrainFill.GenerateBlockData(chunk);
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var block = chunk.GetLocalBlock(x, y, z);
                if (block == Basalt) basaltOrOre++;
                if (block != VulcaniteGenerator.OreBlock) continue;

                basaltOrOre++;
                int wx = cx * Chunk.Size + x, wy = chunk.LowerBoundY + y, wz = cz * Chunk.Size + z;
                Assert.True(VulcaniteGenerator.HasOre(wx, wy, wz), $"ore off the vein field at {wx},{wy},{wz}");
                var laid = HellGenerator.InHell(wy) ? HellGenerator.BlockAt(wx, wy, wz) == Basalt : BarrierGenerator.IsBarrier(wx, wy, wz);
                Assert.True(laid, $"ore where neither the band nor hell laid basalt at {wx},{wy},{wz}");
            }
        }

        Assert.True(basaltOrOre > 0, "the sampled chunks hold no basalt, so the rule was never tested");
    }

    [Fact]
    public void TheVeinsFollowTheWorldSeed()
    {
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
            var first = Cells();
            WorldSeed.Apply(WorldSeed.Default + 7);
            Assert.False(first.SetEquals(Cells()));
            WorldSeed.Apply(WorldSeed.Default);
            Assert.True(first.SetEquals(Cells()));
            Assert.NotEmpty(first);
        }
        finally { WorldSeed.Apply(WorldSeed.Default); }
    }

    private static HashSet<(int, int, int)> Cells()
    {
        var set = new HashSet<(int, int, int)>();
        var shore = ShoreRow(0);
        for (var x = 0; x < 64; x++)
        for (var z = 0; z < 64; z++)
        for (var y = shore; y < shore + 4; y++)
            if (VulcaniteGenerator.HasOre(x, y, z)) set.Add((x, y, z));
        return set;
    }
}
