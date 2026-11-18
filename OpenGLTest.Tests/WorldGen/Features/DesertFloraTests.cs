using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using VoxelEngine.Saves;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen.Features;

/// <summary>Real Dynamic desert sites and the local stamp's ground, core and y-boundary rules.</summary>
[Collection("Worldgen seed")]
public sealed class DesertFloraTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly (long Builds, long Biomes) _coreStart;
    private static int _siteWindowReads;
    // A S3a photographed desert fringe, used as a bounded candidate window rather than a
    // claim that any particular final S3b flower already exists at the camera coordinate.
    private const int WindowX = -7789;
    private const int WindowZ = -7815;
    // One shared pass bounds the candidate window to 2048 entries across all tests.
    private static readonly Lazy<((int X, int Z) Small, (int X, int Z) Bloom, (int X, int Z) Flower)> Sites = new(FindSites);

    public DesertFloraTests(ITestOutputHelper output)
    {
        _output = output;
        TestContent.EnsureLoaded();
        WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
        _coreStart = TerrainErg.CoreReadCounts;
        TerrainErg.ReadCountsEnabled = true;
    }

    public void Dispose()
    {
        var (builds, biomes) = TerrainErg.CoreReadCounts;
        _output.WriteLine($"S3b cumulative site_window_reads={Volatile.Read(ref _siteWindowReads)} core_builds={builds - _coreStart.Builds} stored_biome_reads={biomes - _coreStart.Biomes}");
        TerrainErg.ReadCountsEnabled = false;
        WorldSeed.Apply(WorldSeed.Default);
    }

    private static (int X, int Z) Site(ushort wanted, bool blossom = false) =>
        wanted == DesertFloraGenerator.DesertFlowerBlock ? Sites.Value.Flower
        : blossom ? Sites.Value.Bloom : Sites.Value.Small;

    private static ((int X, int Z) Small, (int X, int Z) Bloom, (int X, int Z) Flower) FindSites()
    {
        (int X, int Z)? small = null;
        (int X, int Z)? bloom = null;
        (int X, int Z)? flower = null;
        for (var x = WindowX; x < WindowX + 64; x++)
        for (var z = WindowZ; z < WindowZ + 32; z++)
        {
            Interlocked.Increment(ref _siteWindowReads);
            var candidate = DesertFloraGenerator.CandidateAt(x, z);
            if (candidate != DesertFloraGenerator.SmallCactusBlock
                && candidate != DesertFloraGenerator.DesertFlowerBlock) continue;
            var column = ColumnCache.Get(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
            var local = ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);
            if (column.Biomes[local] != Biome.Desert || column.DirtDepth[local] == 0
                || TerrainErg.IsCore(x >> Chunk.SizeShift, z >> Chunk.SizeShift))
                continue;
            var height = column.Heights[local];
            if (TerrainGenerator.IsBeachHeight(height) || TerrainGenerator.IsWaterColumn(x, z, height)) continue;
            if (candidate == DesertFloraGenerator.DesertFlowerBlock
                && (height & Chunk.SizeMask) < Chunk.Size - 1)
                flower ??= (x, z);
            else if ((height & Chunk.SizeMask) < Chunk.Size - 1)
            {
                small ??= (x, z);
                if ((height & Chunk.SizeMask) < Chunk.Size - 2 && DesertFloraGenerator.BlossomsAt(x, z))
                    bloom ??= (x, z);
            }
            if (small.HasValue && bloom.HasValue && flower.HasValue)
                return (small.Value, bloom.Value, flower.Value);
        }
        throw new InvalidOperationException("The frozen 64x32 fringe lacks a grounded small cactus, blossom, or desert flower site.");
    }

    private static (ushort[] Blocks, ColumnData Column, int WorldY0, int LocalY) SandAt(int x, int z, int groundY)
    {
        var cellCount = Chunk.Size * Chunk.Size;
        var heights = new int[cellCount];
        Array.Fill(heights, groundY);
        var biomes = new Biome[cellCount];
        Array.Fill(biomes, Biome.Desert);
        var column = new ColumnData(heights, new byte[cellCount], new float[cellCount], new float[cellCount], biomes);
        var worldY0 = groundY & ~Chunk.SizeMask;
        var localY = groundY - worldY0;
        var blocks = new ushort[Chunk.BlockCount];
        blocks[Chunk.Idx(x & Chunk.SizeMask, localY, z & Chunk.SizeMask)] = ContentRegistry.RequireBlock("sand").Id;
        return (blocks, column, worldY0, localY);
    }

    [Fact]
    public void ActualSandAndAirAdmitBothDesertPlantsButWrongGroundOrOccupiedAirDoNot()
    {
        foreach (var wanted in new[] { DesertFloraGenerator.SmallCactusBlock, DesertFloraGenerator.DesertFlowerBlock })
        {
            var (x, z) = Site(wanted);
            // High and dry with y=height+1 inside this chunk; the actual block array owns
            // the ground/occupancy, so candidate climate alone cannot create a plant.
            var (blocks, column, y0, groundLy) = SandAt(x, z, 157);
            var lx = x & Chunk.SizeMask;
            var lz = z & Chunk.SizeMask;
            DesertFloraGenerator.Stamp(blocks, column, x & ~Chunk.SizeMask, y0, z & ~Chunk.SizeMask);
            Assert.Equal(wanted, blocks[Chunk.Idx(lx, groundLy + 1, lz)]);

            var stone = ContentRegistry.RequireBlock("stone").Id;
            (blocks, column, y0, groundLy) = SandAt(x, z, 157);
            blocks[Chunk.Idx(lx, groundLy, lz)] = stone;
            DesertFloraGenerator.Stamp(blocks, column, x & ~Chunk.SizeMask, y0, z & ~Chunk.SizeMask);
            Assert.Equal(BlockType.Air, blocks[Chunk.Idx(lx, groundLy + 1, lz)]);

            (blocks, column, y0, groundLy) = SandAt(x, z, 157);
            blocks[Chunk.Idx(lx, groundLy + 1, lz)] = stone;
            DesertFloraGenerator.Stamp(blocks, column, x & ~Chunk.SizeMask, y0, z & ~Chunk.SizeMask);
            Assert.Equal(stone, blocks[Chunk.Idx(lx, groundLy + 1, lz)]);
        }
    }

    [Fact]
    public void BlossomRequiresNewBaseInSameVerticalChunk()
    {
        var (x, z) = Site(DesertFloraGenerator.SmallCactusBlock, blossom: true);
        var lx = x & Chunk.SizeMask;
        var lz = z & Chunk.SizeMask;
        var x0 = x & ~Chunk.SizeMask;
        var z0 = z & ~Chunk.SizeMask;
        var (blocks, column, y0, groundLy) = SandAt(x, z, 157); // base at ly14, blossom at ly15
        DesertFloraGenerator.Stamp(blocks, column, x0, y0, z0);
        Assert.Equal(DesertFloraGenerator.SmallCactusBlock, blocks[Chunk.Idx(lx, groundLy + 1, lz)]);
        Assert.Equal(DesertFloraGenerator.CactusFlowerBlock, blocks[Chunk.Idx(lx, groundLy + 2, lz)]);

        (blocks, column, y0, groundLy) = SandAt(x, z, 158); // base at ly15, no local upper cell
        DesertFloraGenerator.Stamp(blocks, column, x0, y0, z0);
        Assert.Equal(DesertFloraGenerator.SmallCactusBlock, blocks[Chunk.Idx(lx, groundLy + 1, lz)]);
        Assert.DoesNotContain(DesertFloraGenerator.CactusFlowerBlock, blocks);

        (blocks, column, y0, groundLy) = SandAt(x, z, SurfaceClearance.HighestWrittenY - 1);
        DesertFloraGenerator.Stamp(blocks, column, x0, y0, z0);
        Assert.Equal(DesertFloraGenerator.SmallCactusBlock, blocks[Chunk.Idx(lx, groundLy + 1, lz)]);
        Assert.DoesNotContain(DesertFloraGenerator.CactusFlowerBlock, blocks);
    }

    [Fact]
    public void RealCoreAndWrongStoredBiomeLeavePreparedSandBare()
    {
        const int coreX = -8184;
        const int coreZ = 2312;
        Assert.True(TerrainErg.IsCore(coreX >> Chunk.SizeShift, coreZ >> Chunk.SizeShift));
        var (blocks, column, y0, groundLy) = SandAt(coreX, coreZ, 157);
        DesertFloraGenerator.Stamp(blocks, column, coreX & ~Chunk.SizeMask, y0, coreZ & ~Chunk.SizeMask);
        Assert.Equal(BlockType.Air, blocks[Chunk.Idx(coreX & Chunk.SizeMask, groundLy + 1, coreZ & Chunk.SizeMask)]);

        var (x, z) = Site(DesertFloraGenerator.SmallCactusBlock);
        (blocks, column, y0, groundLy) = SandAt(x, z, 157);
        column.Biomes[((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask)] = Biome.Steppe;
        DesertFloraGenerator.Stamp(blocks, column, x & ~Chunk.SizeMask, y0, z & ~Chunk.SizeMask);
        Assert.Equal(BlockType.Air, blocks[Chunk.Idx(x & Chunk.SizeMask, groundLy + 1, z & Chunk.SizeMask)]);
    }

    [Fact]
    public void HardCoreAndLegacyRejectTheSameCandidateAndSameSeedResetRestoresIt()
    {
        var (x, z) = Site(DesertFloraGenerator.SmallCactusBlock);
        var first = DesertFloraGenerator.CandidateAt(x, z);
        WorldSeed.Apply(1337, WorldGenProfile.Legacy);
        Assert.Equal(BlockType.Air, DesertFloraGenerator.CandidateAt(x, z));
        WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
        Assert.Equal(first, DesertFloraGenerator.CandidateAt(x, z));

        // The S3a default-seed core anchor is an entire bare chunk column.
        const int coreX = -8184;
        const int coreZ = 2312;
        Assert.True(TerrainErg.IsCore(coreX >> Chunk.SizeShift, coreZ >> Chunk.SizeShift));
        Assert.Equal(BlockType.Air, DesertFloraGenerator.CandidateAt(coreX, coreZ));
    }

    [Fact]
    public void HardCoreRejectsAnOtherwiseRetainedPlantOnActualGeneratedSand()
    {
        // The endpoints are independently observed core and flowering-fringe columns.
        // Keep each midpoint's actual verdict as an endpoint; no monotone-core assumption.
        var trueCx = -8184 >> Chunk.SizeShift;
        var trueCz = 2312 >> Chunk.SizeShift;
        var falseCx = -7754 >> Chunk.SizeShift;
        var falseCz = -7791 >> Chunk.SizeShift;
        var coreKeys = 0;
        var centerQueries = 0;
        var inspected = 0;
        (int X, int Z, int Height, ushort Plant)? chosen = null;
        coreKeys++;
        var actualTrue = TerrainErg.IsCore(trueCx, trueCz);
        coreKeys++;
        var actualFalse = TerrainErg.IsCore(falseCx, falseCz);
        _output.WriteLine($"S3b core bracket start true=({trueCx},{trueCz}) verdict={actualTrue} false=({falseCx},{falseCz}) verdict={actualFalse}");
        Assert.True(actualTrue && !actualFalse, "The observed core/fringe endpoint verdict changed; this is a selection precondition miss.");

        var steps = 0;
        while (Math.Max(Math.Abs(falseCx - trueCx), Math.Abs(falseCz - trueCz)) > 1 && steps < 12)
        {
            var middleCx = trueCx + (falseCx - trueCx) / 2;
            var middleCz = trueCz + (falseCz - trueCz) / 2;
            coreKeys++;
            if (TerrainErg.IsCore(middleCx, middleCz))
                (trueCx, trueCz) = (middleCx, middleCz);
            else
                (falseCx, falseCz) = (middleCx, middleCz);
            steps++;
        }
        _output.WriteLine($"S3b core bracket end true=({trueCx},{trueCz}) false=({falseCx},{falseCz}) steps={steps}");
        Assert.True(Math.Max(Math.Abs(falseCx - trueCx), Math.Abs(falseCz - trueCz)) <= 1,
            "The 12-step actual-verdict bracket did not reach adjacent columns; this is a selection miss.");

        var keys = new List<(int Cx, int Cz, float Retention)>(25);
        for (var dx = -2; dx <= 2; dx++)
        for (var dz = -2; dz <= 2; dz++)
        {
            var cx = trueCx + dx;
            var cz = trueCz + dz;
            coreKeys++;
            if (!TerrainErg.IsCore(cx, cz)) continue;
            centerQueries++;
            keys.Add((cx, cz, TerrainErg.Retention(cx * Chunk.Size + Chunk.Size / 2,
                cz * Chunk.Size + Chunk.Size / 2)));
        }
        keys.Sort(static (a, b) =>
        {
            var byRetention = b.Retention.CompareTo(a.Retention);
            if (byRetention != 0) return byRetention;
            var byX = a.Cx.CompareTo(b.Cx);
            return byX != 0 ? byX : a.Cz.CompareTo(b.Cz);
        });
        _output.WriteLine($"S3b edge true_core_columns={keys.Count} center_retention_min={(keys.Count == 0 ? 0f : keys[^1].Retention):F4} max={(keys.Count == 0 ? 0f : keys[0].Retention):F4}");

        foreach (var (cx, cz, _) in keys)
        {
            if (chosen.HasValue || inspected == 2048) break;
            var column = ColumnCache.Get(cx, cz);
            for (var lx = 0; lx < Chunk.Size && !chosen.HasValue && inspected < 2048; lx++)
            for (var lz = 0; lz < Chunk.Size && !chosen.HasValue && inspected < 2048; lz++)
            {
                inspected++;
                var local = (lx << Chunk.SizeShift) | lz;
                if (column.Biomes[local] != Biome.Desert) continue;
                var height = column.Heights[local];
                if ((height & Chunk.SizeMask) == Chunk.Size - 1
                    || TerrainGenerator.IsBeachHeight(height)
                    || !SurfaceClearance.Allows(height, 1)) continue;
                var x = cx * Chunk.Size + lx;
                var z = cz * Chunk.Size + lz;
                if (TerrainGenerator.IsWaterColumn(x, z, height)) continue;
                var plant = DesertFloraGenerator.RetainedCandidateAt(x, z);
                if (plant != BlockType.Air)
                    chosen = (x, z, height, plant);
            }
        }

        _output.WriteLine($"S3b core selection keys={coreKeys} center_retention_queries={centerQueries} point_entries={inspected}");
        Assert.True(chosen.HasValue, "No otherwise retained core plant in the bounded 81-key/2048-point selection; this is a site miss.");
        var (siteX, siteZ, siteHeight, retainedPlant) = chosen.Value;
        Assert.True(TerrainErg.IsCore(siteX >> Chunk.SizeShift, siteZ >> Chunk.SizeShift));
        Assert.NotEqual(BlockType.Air, retainedPlant);

        var cy = (siteHeight + 1) >> Chunk.SizeShift;
        var chunk = new Chunk(null!, new Vector3(siteX >> Chunk.SizeShift, cy, siteZ >> Chunk.SizeShift),
            generateBlockData: true);
        var localX = siteX & Chunk.SizeMask;
        var localZ = siteZ & Chunk.SizeMask;
        var baseY = (siteHeight + 1) & Chunk.SizeMask;
        var ground = chunk.GetLocalBlock(localX, baseY - 1, localZ);
        var baseBlock = chunk.GetLocalBlock(localX, baseY, localZ);
        _output.WriteLine($"S3b core site x={siteX} z={siteZ} cx={siteX >> Chunk.SizeShift} cy={cy} cz={siteZ >> Chunk.SizeShift} height={siteHeight} retained={SaveNames.NameOf(retainedPlant)}({retainedPlant}) ground={SaveNames.NameOf(ground)}({ground}) base={SaveNames.NameOf(baseBlock)}({baseBlock})");
        Assert.Equal(ContentRegistry.RequireBlock("sand").Id, ground);
        Assert.Equal(BlockType.Air, baseBlock);
        Assert.Equal(BlockType.Air, DesertFloraGenerator.CandidateAt(siteX, siteZ));
    }

    [Fact]
    public void NormalGeneratedChunksCarryTheGroundedPlantsAndCompactBlossom()
    {
        var (cactusX, cactusZ) = Site(DesertFloraGenerator.SmallCactusBlock, blossom: true);
        var (flowerX, flowerZ) = Site(DesertFloraGenerator.DesertFlowerBlock);
        AssertGenerated(cactusX, cactusZ, DesertFloraGenerator.SmallCactusBlock,
            DesertFloraGenerator.CactusFlowerBlock);
        AssertGenerated(flowerX, flowerZ, DesertFloraGenerator.DesertFlowerBlock, BlockType.Air);

        void AssertGenerated(int x, int z, ushort plant, ushort blossom)
        {
            var cx = x >> Chunk.SizeShift;
            var cz = z >> Chunk.SizeShift;
            var column = ColumnCache.Get(cx, cz);
            var local = ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);
            var height = column.Heights[local];
            var cy = (height + 1) >> Chunk.SizeShift;
            var chunk = new Chunk(null!, new Vector3(cx, cy, cz), generateBlockData: true);
            var ly = height + 1 - (cy << Chunk.SizeShift);
            var lx = x & Chunk.SizeMask;
            var lz = z & Chunk.SizeMask;
            var actualGround = chunk.GetLocalBlock(lx, ly - 1, lz);
            var actualPlant = chunk.GetLocalBlock(lx, ly, lz);
            var actualUpper = ly + 1 < Chunk.Size ? chunk.GetLocalBlock(lx, ly + 1, lz) : BlockType.Air;
            _output.WriteLine($"S3b selected seed={WorldGenerationConfig.Seed} profile={WorldSeed.Profile} x={x} z={z} cx={cx} cy={cy} cz={cz} height={height} ground={SaveNames.NameOf(actualGround)} plant={SaveNames.NameOf(actualPlant)} upper={SaveNames.NameOf(actualUpper)}");
            Assert.Equal(ContentRegistry.RequireBlock("sand").Id, chunk.GetLocalBlock(lx, ly - 1, lz));
            Assert.Equal(plant, chunk.GetLocalBlock(lx, ly, lz));
            if (blossom != BlockType.Air)
                Assert.Equal(blossom, chunk.GetLocalBlock(lx, ly + 1, lz));
        }
    }

    [Fact]
    public void DynamicDesertFloraBandAdmitsTheRealPlantChunkAndMatchesUngatedFill()
    {
        var (x, z) = Site(DesertFloraGenerator.SmallCactusBlock);
        var cx = x >> Chunk.SizeShift;
        var cz = z >> Chunk.SizeShift;
        var column = ColumnCache.Get(cx, cz);
        var local = ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);
        var cy = (column.Heights[local] + 1) >> Chunk.SizeShift;
        var band = DesertFloraGenerator.WriteBand(column);
        Assert.True(band.Reaches(cy << Chunk.SizeShift));

        var position = new Vector3(cx, cy, cz);
        var gated = new Chunk(null!, position, generateBlockData: false);
        ChunkTerrainFill.GenerateBlockData(gated, stampGates: true);
        var ungated = new Chunk(null!, position, generateBlockData: false);
        ChunkTerrainFill.GenerateBlockData(ungated, stampGates: false);
        Assert.Equal(DesertFloraGenerator.SmallCactusBlock,
            gated.GetLocalBlock(x & Chunk.SizeMask, (column.Heights[local] + 1) & Chunk.SizeMask, z & Chunk.SizeMask));
        Assert.True(gated.Storage.ToDense().AsSpan().SequenceEqual(ungated.Storage.ToDense()));
    }
}
