using System;
using System.Numerics;
using VoxelEngine.Content;
using VoxelEngine.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen.Ocean;

/// <summary>Opt-in normal and planner-probe observation at one frozen natural coral floor.</summary>
[Collection("Worldgen seed")]
public sealed class OceanCoralNaturalProbeDiagnostic(ITestOutputHelper output)
{
    private const string OptInVariable = "VOXEL_S6B_NATURAL_CORAL";
    private const int Cx = -188;
    private const int Cy = -2;
    private const int Cz = 244;
    private const int LocalX = 0;
    private const int LocalFloorY = 9;
    private const int LocalZ = 1;

    [OptInNaturalCoralFact]
    public void FrozenCoralFloorMatchesNormalAndPlannerProbe()
    {
        var previousSeed = WorldSeed.Value;
        var previousProfile = WorldSeed.Profile;
        var applied = false;
        var ownColumnGets = 0;
        var normalFillAttempts = 0;
        var probeConstructorAttempts = 0;
        var probeFillAttempts = 0;
        var directCellReads = 0;

        try
        {
            TestContent.EnsureLoaded();
            var coral = ContentRegistry.RequireBlock("coral_amber").Id;
            var water = ContentRegistry.RequireBlock("water").Id;
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            applied = true;

            ownColumnGets++;
            var column = ColumnCache.Get(Cx, Cz);
            var index = (LocalX << Chunk.SizeShift) | LocalZ;
            var height = column.Heights[index];
            var bed = column.Bed[index];
            var waterLevel = column.WaterLevel[index];
            var biome = column.Biomes[index];
            output.WriteLine($"S6B_CORAL_METADATA world=(-3008,-23,3905) key=({Cx},{Cy},{Cz}) worldY0={Cy * Chunk.Size} "
                + $"local=({LocalX},{LocalFloorY},{LocalZ}) h={height} bed={bed} "
                + $"waterLevel={waterLevel} biome={biome}");
            Assert.Equal(-23, height);
            Assert.Equal(-23, bed);
            Assert.Equal(WorldGenerationConfig.SeaLevel, waterLevel);
            Assert.Equal(Biome.Ocean, biome);

            var key = new Vector3(Cx, Cy, Cz);
            normalFillAttempts++; // Constructor performs the one normal fill.
            var normal = new Chunk(null!, key, generateBlockData: true);
            probeConstructorAttempts++;
            var probe = new Chunk(null!, key, generateBlockData: false);
            probeFillAttempts++; // Planner probe owns its scratch and omits lake planning.
            ChunkTerrainFill.GenerateBlockData(probe, generateLakes: false);

            var normalReady = normal.IsBlockDataReady;
            var probeReady = probe.IsBlockDataReady;
            var normalFloor = ReadBlock(normal, LocalFloorY);
            var normalFloorState = ReadState(normal, LocalFloorY);
            var normalAbove = ReadBlock(normal, LocalFloorY + 1);
            var normalAboveState = ReadState(normal, LocalFloorY + 1);
            var probeFloor = ReadBlock(probe, LocalFloorY);
            var probeFloorState = ReadState(probe, LocalFloorY);
            var probeAbove = ReadBlock(probe, LocalFloorY + 1);
            var probeAboveState = ReadState(probe, LocalFloorY + 1);
            output.WriteLine($"S6B_CORAL_ACTUAL normalReady={normalReady} probeReady={probeReady} "
                + $"normalFloor={normalFloor}/{Name(normalFloor)}:{normalFloorState} "
                + $"normalAbove={normalAbove}/{Name(normalAbove)}:{normalAboveState} "
                + $"probeFloor={probeFloor}/{Name(probeFloor)}:{probeFloorState} "
                + $"probeAbove={probeAbove}/{Name(probeAbove)}:{probeAboveState}");

            Assert.True(normalReady && probeReady, "Both normal and planner-probe chunks must finish.");
            Assert.Equal(coral, normalFloor);
            Assert.Equal((byte)0, normalFloorState);
            Assert.Equal(water, normalAbove);
            Assert.Equal((byte)0, normalAboveState);
            Assert.Equal(coral, probeFloor);
            Assert.Equal((byte)0, probeFloorState);
            Assert.Equal(water, probeAbove);
            Assert.Equal((byte)0, probeAboveState);
            Assert.Equal(normalFloor, probeFloor);
            Assert.Equal(normalFloorState, probeFloorState);
            Assert.Equal(normalAbove, probeAbove);
            Assert.Equal(normalAboveState, probeAboveState);
        }
        finally
        {
            try
            {
                output.WriteLine($"S6B_CORAL_LEDGER seed=1337 profile=Dynamic key=({Cx},{Cy},{Cz}) "
                    + $"ownColumnGets={ownColumnGets}/1 normalFillAttempts={normalFillAttempts}/1 "
                    + $"probeConstructorAttempts={probeConstructorAttempts}/1 "
                    + $"probeFillAttempts={probeFillAttempts}/1 directCellReads={directCellReads}/8");
            }
            finally
            {
                if (applied) WorldSeed.Apply(previousSeed, previousProfile);
            }
        }

        ushort ReadBlock(Chunk chunk, int localY)
        {
            directCellReads++;
            return chunk.GetLocalBlock(LocalX, localY, LocalZ);
        }

        byte ReadState(Chunk chunk, int localY)
        {
            directCellReads++;
            return chunk.GetLocalState(LocalX, localY, LocalZ);
        }
    }

    private static string Name(ushort id) => id == 0 ? "air" : BlockType.ById[id]?.Name ?? $"id:{id}";

    /// <summary>Skips before any content, seed, cache or fill work unless explicitly requested.</summary>
    public sealed class OptInNaturalCoralFactAttribute : FactAttribute
    {
        public OptInNaturalCoralFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
                Skip = $"Set {OptInVariable}=1 for the one frozen coral normal/probe observation.";
        }
    }
}
