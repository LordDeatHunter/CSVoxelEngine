using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using VoxelEngine.WorldGen.Features.Underground;
using VoxelEngine.WorldGen.Hell;
using VoxelEngine.WorldGen.Sky;
using Xunit;
using Stage = VoxelEngine.WorldGen.WorldgenStageTimer.Stage;

namespace VoxelEngine.Tests.WorldGen;

/// <summary>
/// The chunk-level feature stamp gates (PERF_TODO G6): <see cref="ChunkTerrainFill.GenerateBlockData"/>
/// skips a stamp whose <see cref="StampBand"/> misses the chunk, which must never change a generated
/// chunk. Three rules lock that down: the gated fill is byte-identical to the ungated one
/// (<c>stampGates: false</c>) over the <see cref="StampGateSample"/> chunks; every stamp, run on the
/// exact input the fill hands it, changes cells only inside its own band; and a band reaches exactly
/// the chunks that hold one of its cells.
///
/// The default run covers the default sample and stays fast. The exhaustive proof (thousands of
/// chunks, and the identity comparison repeated under a second seed) runs with the sample's
/// environment variable set:
/// <c>VOXEL_STAMP_GATES_EXHAUSTIVE=1 DOTNET_ROLL_FORWARD=LatestMajor ~/.dotnet/dotnet.exe test
/// OpenGLTest.Tests/VoxelEngine.Tests.csproj -c Release --filter FullyQualifiedName~StampGateTests</c>.
/// </summary>
[Collection("Worldgen seed")]
public sealed class StampGateTests
{
    // A second world seed for the exhaustive identity pass; any seed other than the default works
    private const int SecondSeed = WorldSeed.Default + 7919;

    private static readonly Lazy<List<Vector3>> DefaultSeedSample = new(() => StampGateSample.Chunks(StampGateSample.Exhaustive));

    public StampGateTests() => TestContent.EnsureLoaded();

    /// <summary>One feature stamp called directly, in the shape every gated stamp fits.</summary>
    private delegate void StampCall(byte[] blocks, ref byte[]? state, ColumnData column, int x0, int y0, int z0);

    /// <summary>
    /// A gated stamp: the stage it runs in, its band over (own column, neighborhood max height), the
    /// direct call, and whether the sample must see it write at least once.
    /// </summary>
    private sealed record Feature(string Name, Stage Stage, Func<ColumnData, int, StampBand> Band, StampCall Stamp,
        bool MustWrite);

    private static readonly Feature[] Features =
    [
        new("FallenLogs", Stage.FallenLogs, (_, nb) => FallenLogGenerator.WriteBand(nb),
            (byte[] b, ref byte[]? s, ColumnData _, int x, int y, int z) => FallenLogGenerator.Stamp(b, ref s, x, y, z), true),
        new("Boulders", Stage.Boulders, (_, nb) => BoulderGenerator.WriteBand(nb),
            (byte[] b, ref byte[]? _, ColumnData _, int x, int y, int z) => BoulderGenerator.Stamp(b, x, y, z), true),
        new("Cairns", Stage.Cairns, (c, _) => CairnGenerator.WriteBand(c),
            (byte[] b, ref byte[]? _, ColumnData _, int x, int y, int z) => CairnGenerator.Stamp(b, x, y, z), false),
        new("RuinedPillars", Stage.RuinedPillars, (c, _) => RuinedPillarGenerator.WriteBand(c),
            (byte[] b, ref byte[]? _, ColumnData _, int x, int y, int z) => RuinedPillarGenerator.Stamp(b, x, y, z), false),
        new("DesertWells", Stage.DesertWells, (_, nb) => DesertWellGenerator.WriteBand(nb),
            (byte[] b, ref byte[]? _, ColumnData _, int x, int y, int z) => DesertWellGenerator.Stamp(b, x, y, z), false),
        new("Geodes", Stage.Geodes, (_, _) => GeodeGenerator.WriteBand(),
            (byte[] b, ref byte[]? _, ColumnData _, int x, int y, int z) => GeodeGenerator.Stamp(b, x, y, z), true),
        new("Meteors", Stage.Meteors, (c, _) => MeteorGenerator.WriteBand(c),
            (byte[] b, ref byte[]? _, ColumnData c, int x, int y, int z) => MeteorGenerator.Stamp(b, c, x, y, z), true),
        new("Reeds", Stage.Reeds, (c, _) => ReedGenerator.WriteBand(c),
            (byte[] b, ref byte[]? _, ColumnData c, int x, int y, int z) => ReedGenerator.Stamp(b, c, x, y, z), false),
        new("LilyPads", Stage.LilyPads, (c, _) => LilyPadGenerator.WriteBand(c),
            (byte[] b, ref byte[]? _, ColumnData c, int x, int y, int z) => LilyPadGenerator.Stamp(b, c, x, y, z), false),
        new("GlowMushrooms", Stage.GlowMushrooms, (c, _) => GlowMushroomGenerator.WriteBand(c),
            (byte[] b, ref byte[]? _, ColumnData c, int x, int y, int z) => GlowMushroomGenerator.Stamp(b, c, x, y, z), false),
        new("CaveFormations", Stage.CaveFormations, (c, _) => CaveFormationGenerator.WriteBand(c),
            (byte[] b, ref byte[]? _, ColumnData c, int x, int y, int z) => CaveFormationGenerator.Stamp(b, c, x, y, z), true),
        new("UndergroundPools", Stage.UndergroundPools, (c, _) => UndergroundPoolGenerator.WriteBand(c),
            (byte[] b, ref byte[]? _, ColumnData c, int x, int y, int z) => UndergroundPoolGenerator.Stamp(b, c, x, y, z), false),
        new("IceSpikes", Stage.IceSpikes, (c, _) => IceSpikeGenerator.WriteBand(c),
            (byte[] b, ref byte[]? _, ColumnData _, int x, int y, int z) => IceSpikeGenerator.Stamp(b, x, y, z), false),
        new("SnowGround", Stage.Snow, (c, _) => SnowGenerator.GroundWriteBand(c),
            (byte[] b, ref byte[]? _, ColumnData c, int x, int y, int z) => SnowGenerator.StampGround(b, c, x, y, z), true),
        new("Pebbles", Stage.Pebbles, (c, _) => PebbleGenerator.WriteBand(c),
            (byte[] b, ref byte[]? _, ColumnData c, int x, int y, int z) => PebbleGenerator.Stamp(b, c, x, y, z), true),
        new("Cotton", Stage.Cotton, (c, _) => CottonGenerator.WriteBand(c),
            (byte[] b, ref byte[]? _, ColumnData c, int x, int y, int z) => CottonGenerator.Stamp(b, c, x, y, z), false),
        new("Flowers", Stage.Flowers, (c, _) => FlowerGenerator.WriteBand(c),
            (byte[] b, ref byte[]? _, ColumnData c, int x, int y, int z) => FlowerGenerator.Stamp(b, c, x, y, z), true),
        new("Foliage", Stage.Foliage, (c, _) => FoliageGenerator.WriteBand(c),
            (byte[] b, ref byte[]? _, ColumnData c, int x, int y, int z) => FoliageGenerator.Stamp(b, c, x, y, z), true),
        new("Clouds", Stage.Clouds, (_, _) => CloudPatchGenerator.WriteBand(),
            (byte[] b, ref byte[]? _, ColumnData _, int x, int y, int z) => CloudPatchGenerator.Stamp(b, x, y, z), true),
        new("Barrier", Stage.Barrier, (_, _) => BarrierGenerator.WriteBand(),
            (byte[] b, ref byte[]? _, ColumnData _, int x, int y, int z) => BarrierGenerator.Stamp(b, x, y, z), true),
        new("Hell", Stage.Hell, (_, _) => HellGenerator.WriteBand(),
            (byte[] b, ref byte[]? _, ColumnData _, int x, int y, int z) => HellGenerator.Stamp(b, x, y, z), true),
    ];

    private static Chunk Generate(Vector3 pos, WorldgenStageTimer? timer, bool stampGates)
    {
        var chunk = new Chunk(null!, pos, generateBlockData: false);
        ChunkTerrainFill.GenerateBlockData(chunk, timer, stampGates);
        return chunk;
    }

    private static bool SameOrBothNull<T>(T[]? a, T[]? b) =>
        a is null ? b is null : b is not null && a.SequenceEqual(b);

    [Fact]
    public void TheGatedFillIsByteIdenticalToTheUngatedFill()
    {
        var rejected = new bool[Features.Length];
        var admitted = new bool[Features.Length];

        CompareGatedWithUngated(DefaultSeedSample.Value, rejected, admitted);

        if (StampGateSample.Exhaustive)
        {
            try
            {
                WorldSeed.Apply(SecondSeed);
                CompareGatedWithUngated(StampGateSample.Chunks(exhaustive: true), rejected, admitted);
            }
            finally
            {
                WorldSeed.Apply(WorldSeed.Default);
            }
        }

        // Otherwise the comparison proves nothing about that gate: it must both skip and run
        for (var f = 0; f < Features.Length; f++)
        {
            Assert.True(rejected[f], $"{Features[f].Name}: its gate never skipped a filled sample chunk");
            Assert.True(admitted[f], $"{Features[f].Name}: its gate never admitted a filled sample chunk");
        }
    }

    private static void CompareGatedWithUngated(List<Vector3> positions, bool[] rejected, bool[] admitted)
    {
        var timer = new WorldgenStageTimer();
        foreach (var pos in positions)
        {
            timer.Clear();
            var gated = Generate(pos, timer, stampGates: true);
            var ungated = Generate(pos, null, stampGates: false);

            Assert.True(gated.Blocks.SequenceEqual(ungated.Blocks), $"blocks differ at {pos}");
            Assert.True(SameOrBothNull(gated.StateArray, ungated.StateArray), $"state differs at {pos}");
            Assert.True(gated.SolidCount == ungated.SolidCount, $"solid count differs at {pos}");
            Assert.True(gated.SurfaceMinY == ungated.SurfaceMinY, $"surface min y differs at {pos}");
            Assert.True(gated.IsBlockDataReady == ungated.IsBlockDataReady, $"readiness differs at {pos}");
            Assert.True(SameOrBothNull(gated.FluidFrontier, ungated.FluidFrontier), $"fluid frontier differs at {pos}");

            // Only a filled chunk, or a sky chunk in the cloud slab (whose short path skips every other
            // stamp), ever reaches the stamps, so only those say anything about a gate
            if (timer.MarksOf(Stage.Fill) == 0 && timer.MarksOf(Stage.Clouds) == 0) continue;

            var column = ColumnCache.Get((int)pos.X, (int)pos.Z);
            var neighborhood = ChunkTerrainFill.NeighborhoodMaxHeight((int)pos.X, (int)pos.Z);
            for (var f = 0; f < Features.Length; f++)
            {
                if (Features[f].Band(column, neighborhood).Reaches(gated.LowerBoundY))
                    admitted[f] = true;
                else
                    rejected[f] = true;
            }
        }
    }

    [Fact]
    public void EveryStampWritesOnlyInsideItsWriteBand()
    {
        var wrote = new bool[Features.Length];
        var snapshots = new Dictionary<Stage, (byte[] Blocks, byte[]? State)>();
        Chunk? current = null;
        var timer = new WorldgenStageTimer
        {
            AfterMark = s => snapshots[s] = ((byte[])current!.Blocks.Clone(), Clone(current.StateArray)),
        };

        foreach (var pos in DefaultSeedSample.Value)
        {
            snapshots.Clear();
            current = new Chunk(null!, pos, generateBlockData: false);
            ChunkTerrainFill.GenerateBlockData(current, timer, stampGates: false);
            if (!snapshots.ContainsKey(Stage.Fill)) continue;

            var cx = (int)pos.X;
            var cz = (int)pos.Z;
            var column = ColumnCache.Get(cx, cz);
            var neighborhood = ChunkTerrainFill.NeighborhoodMaxHeight(cx, cz);
            var x0 = cx * Chunk.Size;
            var y0 = current.LowerBoundY;
            var z0 = cz * Chunk.Size;

            for (var f = 0; f < Features.Length; f++)
            {
                var feature = Features[f];
                var (inBlocks, inState) = snapshots[feature.Stage - 1];
                var blocks = (byte[])inBlocks.Clone();
                var state = Clone(inState);
                feature.Stamp(blocks, ref state, column, x0, y0, z0);

                var band = feature.Band(column, neighborhood);
                for (var i = 0; i < blocks.Length; i++)
                {
                    if (blocks[i] == inBlocks[i] && StateAt(state, i) == StateAt(inState, i)) continue;

                    wrote[f] = true;
                    var ly = (i >> Chunk.SizeShift) & Chunk.SizeMask;
                    Assert.True(band.Contains(y0 + ly),
                        $"{feature.Name} wrote cell {i} (world y {y0 + ly}) of chunk {pos} outside its band [{band.Min}, {band.Max}]");
                }
            }
        }

        // A stamp that never writes across the sample proves nothing about its band
        for (var f = 0; f < Features.Length; f++)
            if (Features[f].MustWrite)
                Assert.True(wrote[f], $"{Features[f].Name} never wrote a cell across the sample");
    }

    private static byte[]? Clone(byte[]? array) => (byte[]?)array?.Clone();

    private static byte StateAt(byte[]? state, int i) => state is null ? (byte)0 : state[i];

    [Fact]
    public void ABandReachesExactlyTheChunksThatHoldOneOfItsCells()
    {
        var state = 0x5EED_6A7EUL;
        var sawReach = false;
        var sawMiss = false;

        for (var n = 0; n < 20000; n++)
        {
            var worldY0 = ((int)(Next(ref state) % 512) - 256) * Chunk.Size;
            var band = DrawBand(ref state, worldY0);

            var holdsCell = false;
            for (var y = worldY0; y < worldY0 + Chunk.Size; y++)
                holdsCell |= band.Contains(y);

            Assert.True(band.Reaches(worldY0) == holdsCell,
                $"band [{band.Min}, {band.Max}] at chunk floor {worldY0}: Reaches {band.Reaches(worldY0)}, holds a cell {holdsCell}");
            Assert.True(StampBand.Everywhere.Reaches(worldY0), $"the everywhere band misses chunk floor {worldY0}");
            sawReach |= holdsCell;
            sawMiss |= !holdsCell;
        }

        Assert.True(sawReach && sawMiss, "the draw never produced both a reaching and a missing band");
    }

    /// <summary>
    /// A band around a chunk floor: mostly near it (so the floor and ceiling edges are hit often),
    /// sometimes empty, sometimes open below at int.MinValue or open above at int.MaxValue.
    /// </summary>
    private static StampBand DrawBand(ref ulong state, int worldY0)
    {
        var min = worldY0 + (int)(Next(ref state) % 48) - 24;
        var max = min + (int)(Next(ref state) % 40) - 8;
        switch (Next(ref state) % 6)
        {
            case 0:
                min = int.MinValue;
                break;
            case 1:
                max = int.MaxValue;
                break;
            case 2:
                (min, max) = (max, min);
                break;
        }

        return new StampBand(min, max);
    }

    private static ulong Next(ref ulong state) => SplitMix64.Mix(state += 0x9E3779B97F4A7C15UL);
}
