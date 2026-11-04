using System;
using System.Collections.Generic;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features.Underground;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Features.Underground;

/// <summary>
/// The shared ore and pocket lattice (<see cref="OreNoiseLattice"/>, PERF_TODO G3): a chunk's stamp
/// equals the point query cell for cell, across every chunk seam, negative coordinates, partly-host
/// lattice cells and the gravel burial band; the field is a pure function of the seed; and the
/// deposits it draws center freely rather than on lattice points.
/// In the "Worldgen seed" collection because one fact reseeds.
/// </summary>
[Collection("Worldgen seed")]
public sealed class OreNoiseLatticeTests
{
    // 2x2x2 chunk origins spanning negative coordinates and all three face seams
    private static readonly int[] SeamX = { -16, 0 };
    private static readonly int[] SeamY = { -32, -16 };
    private static readonly int[] SeamZ = { -16, 0 };

    // Deposits are centered freely when the phase resultant stays near 1 / sqrt(deposits); a field
    // whose deposits sit on lattice points reads near 1. The ores have hundreds of small deposits,
    // so their bound can be tight; gravel pockets percolate into a few dozen large blobs, whose
    // chance resultant is several times larger.
    private const double OreLockBound = 0.25;
    private const double PocketLockBound = 0.5;

    public OreNoiseLatticeTests() => TestContent.EnsureLoaded();

    [Fact]
    public void AdjacentChunksAgreeWithThePointQueryAcrossEverySeam()
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        AssertStampMatchesPointQuery(MalachiteGenerator.Stamp, MalachiteGenerator.HasOre,
            MalachiteGenerator.OreBlock, stone, "malachite");
        AssertStampMatchesPointQuery(CoalGenerator.Stamp, CoalGenerator.HasOre,
            CoalGenerator.OreBlock, stone, "coal");

        // Hematite's ceiling runs through chunk c, so the volume straddles it; a wider footprint
        // keeps the rare ore present in it
        var c = (WorldGenerationConfig.HematiteMaxY >> 4) << 4;
        int[] wide = { -32, -16, 0, 16 };
        AssertStampMatchesPointQuery(HematiteGenerator.Stamp, HematiteGenerator.HasOre,
            HematiteGenerator.OreBlock, stone, "hematite",
            seamX: wide, seamY: new[] { c - 16, c }, seamZ: wide);
    }

    private static void AssertStampMatchesPointQuery(Action<byte[], int, int, int> stamp,
        Func<int, int, int, bool> hasOre, byte ore, byte stone, string name,
        int[]? seamX = null, int[]? seamY = null, int[]? seamZ = null)
    {
        var oreCells = 0;
        var blocks = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
        foreach (var x0 in seamX ?? SeamX)
        foreach (var y0 in seamY ?? SeamY)
        foreach (var z0 in seamZ ?? SeamZ)
        {
            Array.Fill(blocks, stone);
            stamp(blocks, x0, y0, z0);
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var isOre = blocks[Chunk.Idx(x, y, z)] == ore;
                if (isOre) oreCells++;
                if (isOre == hasOre(x0 + x, y0 + y, z0 + z)) continue;
                Assert.Fail($"{name}: the stamp and the point query disagree at ({x0 + x}, {y0 + y}, {z0 + z})");
            }
        }

        Assert.True(oreCells > 0, $"no {name} in the seam volume, so the comparison proves nothing");
    }

    [Fact]
    public void GravelAgreesWithThePointQueryAcrossSeamsAndTheBurialBand()
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var gravel = GravelPocketGenerator.GravelBlock;
        var sand = GravelPocketGenerator.SandBlock;

        var loose = 0;
        var blocks = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
        foreach (var x0 in SeamX)
        foreach (var z0 in SeamZ)
        {
            var column = HashedColumn(x0, z0);
            foreach (var y0 in new[] { -16, 0, 16, 32 })
            {
                Array.Fill(blocks, stone);
                GravelPocketGenerator.Stamp(blocks, column, x0, y0, z0);
                for (var x = 0; x < Chunk.Size; x++)
                for (var y = 0; y < Chunk.Size; y++)
                for (var z = 0; z < Chunk.Size; z++)
                {
                    var id = blocks[Chunk.Idx(x, y, z)];
                    var isLoose = id == gravel || id == sand;
                    if (isLoose) loose++;
                    var expected = GravelPocketGenerator.HasPocket(x0 + x, y0 + y, z0 + z,
                        HashedHeight(x0 + x, z0 + z));
                    if (isLoose == expected) continue;
                    Assert.Fail($"the pocket stamp and the point query disagree at ({x0 + x}, {y0 + y}, {z0 + z})");
                }
            }
        }

        Assert.True(loose > 0, "no pockets in the seam volume, so the comparison proves nothing");
    }

    /// <summary>A surface height hashed per world column into 0..40.</summary>
    private static int HashedHeight(int x, int z) => (int)(SplitMix64.Hash(x, z, 0x5EED) % 41);

    /// <summary>The chunk column at world origin (x0, z0) under <see cref="HashedHeight"/>.</summary>
    private static ColumnData HashedColumn(int x0, int z0) => Column(x0, z0, HashedHeight);

    /// <summary>A dry chunk column at world origin (x0, z0) whose surface is <paramref name="height"/> of the world column.</summary>
    private static ColumnData Column(int x0, int z0, Func<int, int, int> height)
    {
        var heights = new int[Chunk.Size * Chunk.Size];
        var water = new int[Chunk.Size * Chunk.Size];
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var i = (x << Chunk.SizeShift) | z;
            heights[i] = height(x0 + x, z0 + z);
            water[i] = TerrainGenerator.NoWater;
        }
        return new ColumnData(heights, new byte[heights.Length], new float[heights.Length],
            new float[heights.Length], waterLevel: water);
    }

    [Fact]
    public void OnlyHostCellsAreReplacedAndTheyMatchThePointQuery()
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var dirt = ContentRegistry.RequireBlock("dirt").Id;
        var ore = MalachiteGenerator.OreBlock;

        var oreCells = 0;
        var blocks = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
        foreach (var x0 in SeamX)
        foreach (var y0 in SeamY)
        foreach (var z0 in SeamZ)
        {
            // About half the cells are stone, so most lattice cells hold host and non-host cells alike
            var rng = new SplitMix64(SplitMix64.Hash(x0 ^ (y0 * 31), z0, 0x4057));
            for (var i = 0; i < blocks.Length; i++) blocks[i] = rng.NextFloat() < 0.5f ? stone : dirt;
            var before = (byte[])blocks.Clone();

            MalachiteGenerator.Stamp(blocks, x0, y0, z0);

            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var i = Chunk.Idx(x, y, z);
                if (before[i] == dirt)
                {
                    Assert.True(blocks[i] == dirt, $"a dirt cell changed at ({x0 + x}, {y0 + y}, {z0 + z})");
                    continue;
                }

                var isOre = blocks[i] == ore;
                if (isOre) oreCells++;
                if (isOre == MalachiteGenerator.HasOre(x0 + x, y0 + y, z0 + z)) continue;
                Assert.Fail($"the stamp and the point query disagree on stone at ({x0 + x}, {y0 + y}, {z0 + z})");
            }
        }

        Assert.True(oreCells > 0, "no ore in the partly-stone volume, so the comparison proves nothing");
    }

    [Fact]
    public void TheFieldIsAPureFunctionOfTheSeed()
    {
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
            var first = StampedOre();

            WorldSeed.Apply(WorldSeed.Default + 7);
            var other = StampedOre();
            Assert.False(first.SetEquals(other), "another seed laid exactly the same ore");

            WorldSeed.Apply(WorldSeed.Default);
            var again = StampedOre();
            Assert.True(first.SetEquals(again), "the same seed laid different ore");
            Assert.NotEmpty(first);
        }
        finally
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
    }

    /// <summary>The malachite, coal and hematite cells stamped into the seam chunks, filled with stone.</summary>
    private static HashSet<(int x, int y, int z, byte ore)> StampedOre()
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var cells = new HashSet<(int x, int y, int z, byte ore)>();
        var blocks = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
        foreach (var x0 in SeamX)
        foreach (var y0 in SeamY)
        foreach (var z0 in SeamZ)
        {
            Array.Fill(blocks, stone);
            MalachiteGenerator.Stamp(blocks, x0, y0, z0);
            CoalGenerator.Stamp(blocks, x0, y0, z0);
            HematiteGenerator.Stamp(blocks, x0, y0, z0);
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var id = blocks[Chunk.Idx(x, y, z)];
                if (id != stone) cells.Add((x0 + x, y0 + y, z0 + z, id));
            }
        }
        return cells;
    }

    [Fact]
    public void DepositsAreNotLockedToTheLattice()
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var gravel = GravelPocketGenerator.GravelBlock;
        var sand = GravelPocketGenerator.SandBlock;

        AssertUnlocked("malachite",
            OreScan.Deposits(OreScan.StampedCells(MalachiteGenerator.Stamp, stone, MalachiteGenerator.OreBlock)),
            OreLockBound);
        AssertUnlocked("coal",
            OreScan.Deposits(OreScan.StampedCells(CoalGenerator.Stamp, stone, CoalGenerator.OreBlock)),
            OreLockBound);

        // Every column's surface far above the scan volume, so all of it lies inside the burial band
        var deepColumn = Column(0, 0, (_, _) => 200);
        void StampPockets(byte[] blocks, int x0, int y0, int z0) =>
            GravelPocketGenerator.Stamp(blocks, deepColumn, x0, y0, z0);
        AssertUnlocked("gravel",
            OreScan.Deposits(OreScan.StampedCells(StampPockets, stone, b => b == gravel || b == sand)),
            PocketLockBound);
    }

    private static void AssertUnlocked(string name, List<List<(int x, int y, int z)>> deposits, double bound)
    {
        Assert.True(deposits.Count > 0, $"no {name} deposits, so the lattice check proves nothing");
        for (var axis = 0; axis < 3; axis++)
        {
            var r = OreScan.PhaseResultant(deposits, axis, OreNoiseLattice.Stride);
            Assert.True(r < bound,
                $"{name} deposits lock to the stride-{OreNoiseLattice.Stride} lattice on axis {axis}: " +
                $"R = {r:F3} over N = {deposits.Count} deposits (bound {bound})");
        }
    }
}
