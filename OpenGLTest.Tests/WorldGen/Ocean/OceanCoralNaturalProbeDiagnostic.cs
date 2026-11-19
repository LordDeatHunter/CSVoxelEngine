using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content;
using VoxelEngine.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen.Ocean;

/// <summary>Opt-in normal and planner-probe observations of frozen salt and coral floors.</summary>
[Collection("Worldgen seed")]
public sealed class OceanCoralNaturalProbeDiagnostic(ITestOutputHelper output)
{
    private const string OptInVariable = "VOXEL_S6B_NATURAL_CORAL";
    private const int Cx = -188;
    private const int Cz = 244;
    private const int LocalX = 0;

    private readonly record struct FrozenSite(string Label, int Cy, int WorldFloorY,
        int LocalFloorY, int LocalZ, ushort ExpectedFloor);
    private readonly record struct Cells(ushort Floor, byte FloorState, ushort Above, byte AboveState);
    private readonly record struct Observation(FrozenSite Site, int Height, int Bed, int WaterLevel,
        Biome Biome, bool NormalReady, bool ProbeReady, Cells Normal, Cells Probe);

    [OptInNaturalCoralFact]
    public void FrozenSaltAndCoralFloorsMatchNormalAndPlannerProbe()
    {
        var previousSeed = WorldSeed.Value;
        var previousProfile = WorldSeed.Profile;
        var applied = false;
        var ownColumnGets = 0;
        var normalFillAttempts = 0;
        var probeConstructorAttempts = 0;
        var probeFillAttempts = 0;
        var directCellReads = 0;
        var sitesLogged = 0;

        try
        {
            TestContent.EnsureLoaded();
            var coral = ContentRegistry.RequireBlock("coral_amber").Id;
            var salt = ContentRegistry.RequireBlock("rock_salt").Id;
            var water = ContentRegistry.RequireBlock("water").Id;
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            applied = true;

            ownColumnGets++;
            var column = ColumnCache.Get(Cx, Cz);
            // These are accepted metadata rows, not observed blocks. The first remains the
            // salt-owner control; the shallower gravel-band row is the required coral positive.
            FrozenSite[] sites =
            [
                new("salt-owner", -2, -23, 9, 1, salt),
                new("shallow-coral", -1, -16, 0, 7, coral),
            ];
            var observations = new List<Observation>(sites.Length);
            foreach (var site in sites)
            {
                var index = (LocalX << Chunk.SizeShift) | site.LocalZ;
                var height = column.Heights[index];
                var bed = column.Bed[index];
                var waterLevel = column.WaterLevel[index];
                var biome = column.Biomes[index];
                output.WriteLine($"S6B_COMPANION_METADATA label={site.Label} "
                    + $"world=({Cx * Chunk.Size + LocalX},{site.WorldFloorY},{Cz * Chunk.Size + site.LocalZ}) "
                    + $"key=({Cx},{site.Cy},{Cz}) worldY0={site.Cy * Chunk.Size} "
                    + $"local=({LocalX},{site.LocalFloorY},{site.LocalZ}) "
                    + $"h={height} bed={bed} waterLevel={waterLevel} biome={biome}");

                var key = new Vector3(Cx, site.Cy, Cz);
                normalFillAttempts++; // Constructor performs this site's normal fill.
                var normal = new Chunk(null!, key, generateBlockData: true);
                probeConstructorAttempts++;
                var probe = new Chunk(null!, key, generateBlockData: false);
                probeFillAttempts++; // Planner probe owns scratch and omits lake planning.
                ChunkTerrainFill.GenerateBlockData(probe, generateLakes: false);

                var normalCells = ReadCells(normal, site.LocalFloorY, site.LocalZ);
                var probeCells = ReadCells(probe, site.LocalFloorY, site.LocalZ);
                var observation = new Observation(site, height, bed, waterLevel, biome,
                    normal.IsBlockDataReady, probe.IsBlockDataReady, normalCells, probeCells);
                observations.Add(observation);
                output.WriteLine($"S6B_COMPANION_ACTUAL label={site.Label} "
                    + $"normalReady={observation.NormalReady} probeReady={observation.ProbeReady} "
                    + $"normalFloor={normalCells.Floor}/{Name(normalCells.Floor)}:{normalCells.FloorState} "
                    + $"normalAbove={normalCells.Above}/{Name(normalCells.Above)}:{normalCells.AboveState} "
                    + $"probeFloor={probeCells.Floor}/{Name(probeCells.Floor)}:{probeCells.FloorState} "
                    + $"probeAbove={probeCells.Above}/{Name(probeCells.Above)}:{probeCells.AboveState}");
                sitesLogged++;
            }

            // Both actual locations are logged before either positive material assertion.
            foreach (var observed in observations)
            {
                Assert.Equal(observed.Site.WorldFloorY, observed.Site.Cy * Chunk.Size + observed.Site.LocalFloorY);
                Assert.Equal(observed.Site.WorldFloorY, observed.Height);
                Assert.Equal(observed.Site.WorldFloorY, observed.Bed);
                Assert.Equal(WorldGenerationConfig.SeaLevel, observed.WaterLevel);
                Assert.Equal(Biome.Ocean, observed.Biome);
                Assert.True(observed.NormalReady && observed.ProbeReady,
                    $"Both {observed.Site.Label} chunks must finish.");
                AssertCells(observed.Normal, observed.Site.ExpectedFloor, water);
                AssertCells(observed.Probe, observed.Site.ExpectedFloor, water);
                Assert.Equal(observed.Normal, observed.Probe);
            }
        }
        finally
        {
            try
            {
                output.WriteLine($"S6B_COMPANION_LEDGER seed=1337 profile=Dynamic column=({Cx},{Cz}) "
                    + $"ownColumnGets={ownColumnGets}/1 normalFillAttempts={normalFillAttempts}/2 "
                    + $"probeConstructorAttempts={probeConstructorAttempts}/2 "
                    + $"probeFillAttempts={probeFillAttempts}/2 directCellReads={directCellReads}/16 "
                    + $"sitesLogged={sitesLogged}/2");
            }
            finally
            {
                if (applied) WorldSeed.Apply(previousSeed, previousProfile);
            }
        }

        Cells ReadCells(Chunk chunk, int localY, int localZ)
        {
            directCellReads++;
            var floor = chunk.GetLocalBlock(LocalX, localY, localZ);
            directCellReads++;
            var floorState = chunk.GetLocalState(LocalX, localY, localZ);
            directCellReads++;
            var above = chunk.GetLocalBlock(LocalX, localY + 1, localZ);
            directCellReads++;
            var aboveState = chunk.GetLocalState(LocalX, localY + 1, localZ);
            return new Cells(floor, floorState, above, aboveState);
        }
    }

    private static void AssertCells(Cells actual, ushort expectedFloor, ushort water)
    {
        Assert.Equal(expectedFloor, actual.Floor);
        Assert.Equal((byte)0, actual.FloorState);
        Assert.Equal(water, actual.Above);
        Assert.Equal((byte)0, actual.AboveState);
    }

    private static string Name(ushort id) => id == 0 ? "air" : BlockType.ById[id]?.Name ?? $"id:{id}";

    /// <summary>Skips before any content, seed, cache or fill work unless explicitly requested.</summary>
    public sealed class OptInNaturalCoralFactAttribute : FactAttribute
    {
        public OptInNaturalCoralFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
                Skip = $"Set {OptInVariable}=1 for the frozen salt-owner and coral normal/probe observations.";
        }
    }
}
