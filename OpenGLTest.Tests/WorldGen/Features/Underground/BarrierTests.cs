using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Interaction;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features.Underground;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Features.Underground;

/// <summary>
/// The barrier band (plans/LAYERS.md L-5): nothing outside its config band, a gapless core that
/// takes caves and fluids too, a patchy share that rises into the core from both edges, a stamp
/// that equals its point query across chunk seams, patches that follow the world seed, and a block
/// only the iron pickaxe mines.
/// </summary>
[Collection("Worldgen seed")]
public sealed class BarrierTests
{
    private const int Volume = Chunk.Size * Chunk.Size * Chunk.Size;

    public BarrierTests() => TestContent.EnsureLoaded();

    private static ushort Id(string name) => ContentRegistry.RequireBlock(name).Id;

    [Fact]
    public void NoBarrierOutsideTheBand()
    {
        var stone = Id("stone");
        var blocks = new ushort[Volume];

        // The chunks straddling each edge of the band and the ones just outside it
        int[] floors =
        [
            (WorldGenerationConfig.BarrierTopY >> Chunk.SizeShift) * Chunk.Size,
            ((WorldGenerationConfig.BarrierTopY >> Chunk.SizeShift) + 1) * Chunk.Size,
            (WorldGenerationConfig.BarrierBottomY >> Chunk.SizeShift) * Chunk.Size,
            ((WorldGenerationConfig.BarrierBottomY >> Chunk.SizeShift) - 1) * Chunk.Size,
        ];

        foreach (var y0 in floors)
        for (var x0 = -32; x0 < 32; x0 += Chunk.Size)
        {
            Array.Fill(blocks, stone);
            BarrierGenerator.Stamp(blocks, x0, y0, 0);
            for (var i = 0; i < Volume; i++)
            {
                var wy = y0 + ((i >> Chunk.SizeShift) & Chunk.SizeMask);
                if (wy <= WorldGenerationConfig.BarrierTopY && wy >= WorldGenerationConfig.BarrierBottomY) continue;
                Assert.Equal(stone, blocks[i]);
            }
        }

        for (var x = 0; x < 64; x++)
        for (var z = 0; z < 64; z++)
        {
            Assert.False(BarrierGenerator.IsBarrier(x, WorldGenerationConfig.BarrierTopY + 1, z));
            Assert.False(BarrierGenerator.IsBarrier(x, WorldGenerationConfig.BarrierBottomY - 1, z));
        }
    }

    [Fact]
    public void TheCoreIsGaplessThroughCavesAndFluids()
    {
        // A synthetic chunk entirely inside the core, holding air, water and lava: all of it walls up
        var coreFloor = (WorldGenerationConfig.BarrierFullTopY >> Chunk.SizeShift) * Chunk.Size - Chunk.Size;
        Assert.True(BarrierGenerator.InCore(coreFloor) && BarrierGenerator.InCore(coreFloor + Chunk.Size - 1),
            "the test chunk must lie in the core");
        ushort[] fills = [BlockType.Air, Id("water"), Id("lava"), Id("stone")];
        var blocks = new ushort[Volume];
        for (var i = 0; i < Volume; i++)
            blocks[i] = fills[i % fills.Length];
        BarrierGenerator.Stamp(blocks, 48, coreFloor, -16);
        Assert.All(blocks, b => Assert.Equal(BarrierGenerator.BarrierBlock, b));

        // And a real generated chunk there, caves and all
        var chunk = new Chunk(null!, new Vector3(3, coreFloor >> Chunk.SizeShift, -2), generateBlockData: false);
        ChunkTerrainFill.GenerateBlockData(chunk);
        Assert.All(chunk.Storage.ToDense(), b => Assert.Equal(BarrierGenerator.BarrierBlock, b));
    }

    [Fact]
    public void TheShareRisesIntoTheCoreFromBothEdges()
    {
        // Thirds of each ramp, edge to core: the barrier's share of a wide stone area rises
        var top = WorldGenerationConfig.BarrierTopY;
        var fullTop = WorldGenerationConfig.BarrierFullTopY;
        var bottom = WorldGenerationConfig.BarrierBottomY;
        var fullBottom = WorldGenerationConfig.BarrierFullBottomY;

        AssertRises(Thirds(top - 1, fullTop + 1));
        AssertRises(Thirds(bottom + 1, fullBottom - 1));
    }

    private static IEnumerable<(int From, int To)> Thirds(int edge, int core)
    {
        var step = core > edge ? 1 : -1;
        var length = Math.Abs(core - edge) + 1;
        for (var t = 0; t < 3; t++)
        {
            var from = edge + step * (t * length / 3);
            var to = edge + step * ((t + 1) * length / 3 - 1);
            yield return (from, to);
        }
    }

    private static void AssertRises(IEnumerable<(int From, int To)> bands)
    {
        var previous = -1.0;
        foreach (var (from, to) in bands)
        {
            var share = ShareOver(Math.Min(from, to), Math.Max(from, to));
            Assert.True(share > previous, $"share {share:F3} over y {from}..{to} does not rise past {previous:F3}");
            Assert.True(share > 0.0 && share < 1.0, $"the ramp over y {from}..{to} is not patchy ({share:F3})");
            previous = share;
        }
    }

    private static double ShareOver(int yMin, int yMax)
    {
        var hits = 0;
        var total = 0;
        for (var y = yMin; y <= yMax; y++)
        for (var x = -256; x < 256; x += 4)
        for (var z = -256; z < 256; z += 4)
        {
            total++;
            if (BarrierGenerator.IsBarrier(x, y, z)) hits++;
        }
        return (double)hits / total;
    }

    [Fact]
    public void TheStampEqualsThePointQueryAcrossChunkSeams()
    {
        var stone = Id("stone");
        var barrier = BarrierGenerator.BarrierBlock;
        var blocks = new ushort[Volume];
        var sawBarrier = false;
        var sawStone = false;

        // Two ramp chunks (one per ramp) over a 3x3 block of chunk columns, plus one carved cell
        int[] floors =
        [
            (WorldGenerationConfig.BarrierFullTopY + 10) >> Chunk.SizeShift << Chunk.SizeShift,
            (WorldGenerationConfig.BarrierFullBottomY - 10) >> Chunk.SizeShift << Chunk.SizeShift,
        ];
        foreach (var y0 in floors)
        for (var x0 = -Chunk.Size; x0 <= Chunk.Size; x0 += Chunk.Size)
        for (var z0 = -Chunk.Size; z0 <= Chunk.Size; z0 += Chunk.Size)
        {
            Array.Fill(blocks, stone);
            blocks[Chunk.Idx(5, 5, 5)] = BlockType.Air;
            BarrierGenerator.Stamp(blocks, x0, y0, z0);
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var wy = y0 + y;
                var got = blocks[Chunk.Idx(x, y, z)];
                if (x == 5 && y == 5 && z == 5 && !BarrierGenerator.InCore(wy))
                {
                    Assert.Equal(BlockType.Air, got);
                    continue;
                }

                Assert.Equal(BarrierGenerator.IsBarrier(x0 + x, wy, z0 + z) ? barrier : stone, got);
                sawBarrier |= got == barrier;
                sawStone |= got == stone;
            }
        }

        Assert.True(sawBarrier && sawStone, "the ramp chunks hold no mix of barrier and stone");
    }

    [Fact]
    public void AGeneratedRampChunkFollowsThePointQuery()
    {
        int[] cys =
        [
            (WorldGenerationConfig.BarrierFullTopY + 10) >> Chunk.SizeShift,
            (WorldGenerationConfig.BarrierFullBottomY - 10) >> Chunk.SizeShift,
        ];
        foreach (var cy in cys)
        for (var cx = 0; cx < 2; cx++)
        {
            var chunk = new Chunk(null!, new Vector3(cx, cy, 1), generateBlockData: false);
            ChunkTerrainFill.GenerateBlockData(chunk);
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var block = chunk.GetLocalBlock(x, y, z);
                var field = BarrierGenerator.IsBarrier(cx * Chunk.Size + x, chunk.LowerBoundY + y, Chunk.Size + z);
                if (block == BarrierGenerator.BarrierBlock)
                    Assert.True(field, $"barrier off the field at {x},{y},{z} of chunk y {cy}");
                else if (field)
                    Assert.False(BarrierGenerator.IsHost(block), $"a host cell on the field was left at {x},{y},{z}");
            }
        }
    }

    [Fact]
    public void ThePatchesFollowTheWorldSeed()
    {
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
            var first = RampCells();
            WorldSeed.Apply(WorldSeed.Default + 7);
            Assert.False(first.SetEquals(RampCells()));
            WorldSeed.Apply(WorldSeed.Default);
            Assert.True(first.SetEquals(RampCells()));
            Assert.NotEmpty(first);
        }
        finally { WorldSeed.Apply(WorldSeed.Default); }
    }

    private static HashSet<(int, int, int)> RampCells()
    {
        var set = new HashSet<(int, int, int)>();
        for (var x = 0; x < 48; x++)
        for (var z = 0; z < 48; z++)
        for (var y = WorldGenerationConfig.BarrierFullTopY + 1; y < WorldGenerationConfig.BarrierTopY; y++)
            if (BarrierGenerator.IsBarrier(x, y, z)) set.Add((x, y, z));
        return set;
    }

    [Fact]
    public void OnlyTheIronPickaxeMinesTheBarrier()
    {
        var barrier = BarrierGenerator.BarrierBlock;
        Assert.True(Mining.CanMine(barrier, ContentRegistry.RequireItem("iron_pickaxe")));
        Assert.False(Mining.CanMine(barrier, ContentRegistry.RequireItem("bronze_pickaxe")));
        Assert.False(Mining.CanMine(barrier, null));
        Assert.True(Mining.CanMine(Id("stone"), ContentRegistry.RequireItem("bronze_pickaxe")),
            "the bronze pickaxe must still mine the stone around the band");
    }
}
