using System;
using System.Numerics;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Ocean;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen.Ocean;

/// <summary>Opt-in observation of actual S6a ocean rock in one frozen basin neighborhood.</summary>
[Collection("Worldgen seed")]
public sealed class OceanRockNaturalProbeDiagnostic(ITestOutputHelper output)
{
    private const string OptInVariable = "VOXEL_S6A_NATURAL_ROCK_PROBE";

    // Four fixed 16x16 footprints around the previously filmed seed-1337 Dynamic basin
    // at (-1200, -3000). Their rock-field admission is unknown until this fact runs.
    private static readonly (int Cx, int Cz)[] Footprints =
    [
        (-75, -188), (-74, -188), (-75, -187), (-74, -187),
    ];

    [OptInRockFact]
    public void NaturalRockColumnHasSupportAndMatchesNormalAndPlannerProbe()
    {
        var columnGets = 0;
        var cornerGets = 0;
        var inspected = 0;
        var normalFills = 0;
        var probeFills = 0;
        var cellReads = 0;
        var witnesses = 0;
        var applied = false;
        var previousSeed = WorldSeed.Value;
        var previousProfile = WorldSeed.Profile;

        try
        {
            TestContent.EnsureLoaded();
            var water = TerrainGenerator.SeaBlock;
            var stone = TerrainGenerator.RockBlock;
            var darkRock = TerrainGenerator.CharredStoneBlock;
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            applied = true;

            foreach (var (cx, cz) in Footprints)
            {
                columnGets++;
                var column = ColumnCache.Get(cx, cz);
                var centerFloor = column.Heights[(8 << Chunk.SizeShift) | 8];
                var cy = MathUtils.FloorDiv(centerFloor + 1, Chunk.Size);
                var y0 = cy * Chunk.Size;
                var field = new OceanHabitatField(
                    Corner(cx, cz), Corner(cx + 1, cz),
                    Corner(cx, cz + 1), Corner(cx + 1, cz + 1));
                output.WriteLine($"S6a footprint seed=1337 profile=Dynamic key=({cx},{cy},{cz}) centerFloor={centerFloor} y0={y0} corners=({field.C00},{field.C10},{field.C01},{field.C11})");

                var key = new Vector3(cx, cy, cz);
                normalFills++;
                var normal = new Chunk(null!, key, generateBlockData: true);
                var probe = new Chunk(null!, key, generateBlockData: false);
                probeFills++;
                ChunkTerrainFill.GenerateBlockData(probe, generateLakes: false);
                Assert.True(normal.IsBlockDataReady && probe.IsBlockDataReady);

                for (var x = 0; x < Chunk.Size; x++)
                for (var z = 0; z < Chunk.Size; z++)
                {
                    inspected++;
                    var ci = (x << Chunk.SizeShift) | z;
                    var floorY = column.Heights[ci];
                    var localFloor = floorY - y0;
                    var habitat = field.Classify(true, column.Biomes[ci] == Biome.Ocean,
                        column.WaterLevel[ci], floorY, x + 0.5f, z + 0.5f);
                    output.WriteLine($"S6a site x={cx * Chunk.Size + x} z={cz * Chunk.Size + z} biome={column.Biomes[ci]} bed={column.Bed[ci]} floor={floorY} water={column.WaterLevel[ci]} habitat={habitat} localFloor={localFloor}");
                    if (habitat is not (OceanHabitat.DeepPlain or OceanHabitat.Abyss)
                        || localFloor < 0
                        || localFloor + WorldFeatureConfig.OceanRockColumnMinHeight + 1 >= Chunk.Size)
                        continue;

                    var expectedRock = habitat == OceanHabitat.Abyss ? darkRock : stone;
                    var maxInspectableHeight = Math.Min(WorldFeatureConfig.OceanRockColumnMaxHeight,
                        Chunk.Size - localFloor - 2);
                    var support = ReadBlock(normal, x, localFloor, z);
                    var probeSupport = ReadBlock(probe, x, localFloor, z);
                    var first = ReadBlock(normal, x, localFloor + 1, z);
                    var probeFirst = ReadBlock(probe, x, localFloor + 1, z);
                    if (first != expectedRock) continue;

                    var height = 0;
                    while (height < maxInspectableHeight)
                    {
                        if (ReadBlock(normal, x, localFloor + height + 1, z) != expectedRock) break;
                        if (ReadBlock(probe, x, localFloor + height + 1, z) != expectedRock) break;
                        height++;
                    }
                    output.WriteLine($"S6a candidate x={cx * Chunk.Size + x} z={cz * Chunk.Size + z} key=({cx},{cy},{cz}) support={Name(support)} probeSupport={Name(probeSupport)} first={Name(first)} probeFirst={Name(probeFirst)} rock={Name(expectedRock)} observedHeight={height}");
                    if (height < WorldFeatureConfig.OceanRockColumnMinHeight
                        || support != stone || probeSupport != support || probeFirst != first
                        || floorY + height > WorldGenerationConfig.SeaLevel - 2)
                        continue;

                    for (var step = 1; step <= height; step++)
                    {
                        Assert.Equal(expectedRock, ReadBlock(normal, x, localFloor + step, z));
                        Assert.Equal(expectedRock, ReadBlock(probe, x, localFloor + step, z));
                        Assert.Equal((byte)0, ReadState(normal, x, localFloor + step, z));
                        Assert.Equal((byte)0, ReadState(probe, x, localFloor + step, z));
                    }
                    var capNormal = ReadBlock(normal, x, localFloor + height + 1, z);
                    var capProbe = ReadBlock(probe, x, localFloor + height + 1, z);
                    var capStateNormal = ReadState(normal, x, localFloor + height + 1, z);
                    var capStateProbe = ReadState(probe, x, localFloor + height + 1, z);
                    output.WriteLine($"S6a cap x={cx * Chunk.Size + x} z={cz * Chunk.Size + z} normal={Name(capNormal)}:{capStateNormal} probe={Name(capProbe)}:{capStateProbe}");
                    Assert.Equal(water, capNormal);
                    Assert.Equal(water, capProbe);
                    Assert.Equal((byte)0, capStateNormal);
                    Assert.Equal((byte)0, capStateProbe);
                    witnesses++;
                    output.WriteLine($"S6a WITNESS x={cx * Chunk.Size + x} z={cz * Chunk.Size + z} floor={floorY} height={height} rock={Name(expectedRock)}:0 normalProbeEqual=true");
                    return; // One positive natural witness is the whole observation.
                }
            }

            Assert.Fail("No natural rock witness in the four frozen basin footprints; retain the full miss ledger.");
        }
        finally
        {
            output.WriteLine($"S6a partial ledger seed=1337 profile=Dynamic columnGets={columnGets}/4 cornerGets={cornerGets}/16 inspected={inspected}/1024 normalFills={normalFills}/4 probeFills={probeFills}/4 directCellReads={cellReads} witnesses={witnesses}");
            if (applied) WorldSeed.Apply(previousSeed, previousProfile);
        }

        float Corner(int x, int z)
        {
            cornerGets++;
            return ColumnCache.Get(x, z).CornerWarmth;
        }

        ushort ReadBlock(Chunk chunk, int x, int y, int z)
        {
            cellReads++;
            return chunk.GetLocalBlock(x, y, z);
        }

        byte ReadState(Chunk chunk, int x, int y, int z)
        {
            cellReads++;
            return chunk.GetLocalState(x, y, z);
        }
    }

    private static string Name(ushort id) => id == 0 ? "air" : BlockType.ById[id]?.Name ?? $"id:{id}";

    /// <summary>Discovery skips all generation unless the bounded observation is requested.</summary>
    public sealed class OptInRockFactAttribute : FactAttribute
    {
        public OptInRockFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
                Skip = $"Set {OptInVariable}=1 for the frozen ocean rock normal/probe observation.";
        }
    }
}
