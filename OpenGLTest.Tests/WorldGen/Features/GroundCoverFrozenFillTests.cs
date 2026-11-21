using System;
using System.Numerics;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen.Features;

/// <summary>Real-fill follow-up for the fixed Forest and Steppe classifier observations.</summary>
[Collection("Worldgen seed")]
public sealed class GroundCoverFrozenFillTests : IDisposable
{
    private readonly ITestOutputHelper _output;

    // Fixed from the first seed-1337 Dynamic observation, not a new candidate search.
    private static readonly (int X, int Z, string Plant)[] ForestSites =
    [
        (2, 0, "tall_fern"), (10, 2, "tall_fern"),
        (12, 1, "bush"), (12, 2, "bush"),
        (13, 1, "fern"), (13, 2, "fern"), (14, 1, "fern"),
        (15, 1, "tall_fern"),
    ];

    private static readonly (int X, int Z, string Plant)[] SteppeSites =
    [
        (19, 158, "bush"), (24, 151, "bush"),
        (25, 155, "bush"), (27, 154, "bush"),
    ];

    public GroundCoverFrozenFillTests(ITestOutputHelper output)
    {
        _output = output;
        TestContent.EnsureLoaded();
        WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
    }

    public void Dispose() => WorldSeed.Apply(WorldSeed.Default);

    [Fact]
    public void FrozenForestAndSteppeCandidatesReachRealNormalAndPlannerProbeWriters()
    {
        var grass = ContentRegistry.RequireBlock("grass").Id;
        var tallTop = ContentRegistry.RequireBlock("tall_fern_top").Id;
        var normalFills = 0;
        var probeFills = 0;
        var columnGets = 0;
        var inspected = 0;
        var forestWrites = 0;
        var steppeWrites = 0;
        var tallPairs = 0;
        try
        {
            InspectColumn(0, 0, Biome.Forest, ForestSites);
            InspectColumn(1, 9, Biome.Steppe, SteppeSites);
        }
        finally
        {
            _output.WriteLine($"S4a frozen fills columnGets={columnGets}/2 normal={normalFills}/2 plannerProbe={probeFills}/2 fixedSites={inspected}/12 forestWrites={forestWrites} steppeWrites={steppeWrites} tallPairs={tallPairs}");
        }
        Assert.True(forestWrites > 0, "no fixed Forest candidate became real new ground cover");
        Assert.True(steppeWrites > 0, "no fixed Steppe bush candidate became a real generated bush");

        void InspectColumn(int cx, int cz, Biome wanted, (int X, int Z, string Plant)[] sites)
        {
            columnGets++;
            var column = ColumnCache.Get(cx, cz);
            var pos = new Vector3(cx, 0, cz);
            normalFills++;
            var normal = new Chunk(null!, pos, generateBlockData: true);
            var probe = new Chunk(null!, pos, generateBlockData: false);
            probeFills++;
            ChunkTerrainFill.GenerateBlockData(probe, generateLakes: false);

            var normalDense = normal.Storage.ToDense();
            var probeDense = probe.Storage.ToDense();
            var fullDiff = 0;
            for (var i = 0; i < normalDense.Length; i++)
                if (normalDense[i] != probeDense[i]) fullDiff++;
            _output.WriteLine($"S4a fullfill cx={cx} cz={cz} fullBlockDiff={fullDiff} fullStateSame={Same(normal.StateArray, probe.StateArray)} frontierSame={Same(normal.FluidFrontier, probe.FluidFrontier)} normalReady={normal.IsBlockDataReady} probeReady={probe.IsBlockDataReady} normalSolid={normal.SolidCount} probeSolid={probe.SolidCount} normalSurfaceMin={normal.SurfaceMinY} probeSurfaceMin={probe.SurfaceMinY}");
            Assert.True(normal.IsBlockDataReady && probe.IsBlockDataReady);

            foreach (var (x, z, plantName) in sites)
            {
                inspected++;
                var lx = x - cx * Chunk.Size;
                var lz = z - cz * Chunk.Size;
                var ci = (lx << Chunk.SizeShift) | lz;
                var height = column.Heights[ci];
                var ly = height + 1;
                var ground = normal.GetLocalBlock(lx, ly - 1, lz);
                var baseId = normal.GetLocalBlock(lx, ly, lz);
                var topId = normal.GetLocalBlock(lx, ly + 1, lz);
                var probeGround = probe.GetLocalBlock(lx, ly - 1, lz);
                var probeBase = probe.GetLocalBlock(lx, ly, lz);
                var probeTop = probe.GetLocalBlock(lx, ly + 1, lz);
                var expected = ContentRegistry.RequireBlock(plantName).Id;
                var candidate = DynamicGroundCover.CandidateAt(x, z, column.Biomes[ci]);
                var tuft = FoliageGenerator.TryGetGrassTuft(x, z);
                _output.WriteLine($"S4a fixedsite x={x} z={z} height={height} stored={column.Biomes[ci]} pure={TerrainGenerator.FloraBiomeAt(x, z)} tuft={tuft} candidate={candidate} frozen={plantName} ground={ground} base={baseId} top={topId} probeGround={probeGround} probeBase={probeBase} probeTop={probeTop} state={normal.GetLocalState(lx, ly, lz)} probeState={probe.GetLocalState(lx, ly, lz)}");
                // The lake planner omits cave life, formations and cave lava. At these dry
                // surface cells its plant/ground output should still agree with normal fill.
                Assert.Equal(ground, probeGround);
                Assert.Equal(baseId, probeBase);
                Assert.Equal(topId, probeTop);
                Assert.Equal(normal.GetLocalState(lx, ly, lz), probe.GetLocalState(lx, ly, lz));
                Assert.Equal(normal.GetLocalState(lx, ly + 1, lz), probe.GetLocalState(lx, ly + 1, lz));
                if (column.Biomes[ci] != wanted || !tuft || candidate != expected
                    || ground != grass || baseId != candidate) continue;
                var band = FoliageGenerator.WriteBand(column);
                Assert.True(band.Contains(height + 1), "real new-cover base lies outside the foliage write band");
                if (wanted == Biome.Forest) forestWrites++;
                else steppeWrites++;
                if (baseId == ContentRegistry.RequireBlock("tall_fern").Id)
                {
                    Assert.Equal(tallTop, topId);
                    Assert.True(band.Contains(height + 2), "real tall-fern top lies outside the foliage write band");
                    tallPairs++;
                }
            }
        }
    }

    private static bool Same<T>(T[]? a, T[]? b) where T : IEquatable<T> =>
        a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
}
