using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content;
using VoxelEngine.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen.Ocean;

/// <summary>Opt-in normal and planner-probe blocks at two frozen moss metadata candidates.</summary>
[Collection("Worldgen seed")]
public sealed class OceanMossNaturalProbeDiagnostic(ITestOutputHelper output)
{
    private const string OptInVariable = "VOXEL_S6B_NATURAL_MOSS";

    private readonly record struct FrozenSite(string Label, int Cx, int Cy, int Cz,
        int LocalX, int LocalY, int LocalZ, int WorldFloorY, ushort ExpectedFloor);
    private readonly record struct Cells(ushort Floor, byte FloorState, ushort Above, byte AboveState);
    private readonly record struct Observation(FrozenSite Site, int Height, int Bed,
        int WaterLevel, Biome Biome, byte DirtDepth, bool NormalReady, bool ProbeReady,
        Cells Normal, Cells Probe);

    [OptInNaturalMossFact]
    public void FrozenSeaAndGlowMossFloorsMatchNormalAndPlannerProbe()
    {
        var previousSeed = WorldSeed.Value;
        var previousProfile = WorldSeed.Profile;
        var applied = false;
        var ownColumnGetAttempts = 0;
        var normalFillAttempts = 0;
        var probeConstructorAttempts = 0;
        var probeFillAttempts = 0;
        var directCellGetters = 0;
        var sitesLogged = 0;

        try
        {
            TestContent.EnsureLoaded();
            var seaMoss = ContentRegistry.RequireBlock("sea_moss").Id;
            var glowMoss = ContentRegistry.RequireBlock("glow_moss").Id;
            var water = ContentRegistry.RequireBlock("water").Id;
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            applied = true;

            // Both points are frozen from the eight-column generated metadata observation.
            // The second glow point has an independently rejected OCRK rock-column roll;
            // salt was predicted false. Neither result proves its actual block before fill.
            FrozenSite[] sites =
            [
                new("sea_moss", -188, -1, 220, 4, 13, 15, -3, seaMoss),
                new("glow_moss", -220, -5, 228, 0, 10, 11, -70, glowMoss),
            ];
            var observations = new List<Observation>(sites.Length);
            foreach (var site in sites)
            {
                ownColumnGetAttempts++; // Count before a cold or evicted factory enters.
                var column = ColumnCache.Get(site.Cx, site.Cz);
                var i = (site.LocalX << Chunk.SizeShift) | site.LocalZ;
                var height = column.Heights[i];
                var bed = column.Bed[i];
                var waterLevel = column.WaterLevel[i];
                var biome = column.Biomes[i];
                var dirtDepth = column.DirtDepth[i];
                var worldX = site.Cx * Chunk.Size + site.LocalX;
                var worldZ = site.Cz * Chunk.Size + site.LocalZ;
                output.WriteLine($"S6B_MOSS_PAIR_METADATA label={site.Label} "
                    + $"world=({worldX},{site.WorldFloorY},{worldZ}) "
                    + $"key=({site.Cx},{site.Cy},{site.Cz}) worldY0={site.Cy * Chunk.Size} "
                    + $"local=({site.LocalX},{site.LocalY},{site.LocalZ}) "
                    + $"h={height} bed={bed} water={waterLevel} biome={biome} dirtDepth={dirtDepth}");

                var key = new Vector3(site.Cx, site.Cy, site.Cz);
                normalFillAttempts++; // Constructor performs the normal production fill.
                var normal = new Chunk(null!, key, generateBlockData: true);
                probeConstructorAttempts++;
                var probe = new Chunk(null!, key, generateBlockData: false);
                probeFillAttempts++; // The planner's scratch path omits lake planning.
                ChunkTerrainFill.GenerateBlockData(probe, generateLakes: false);

                var normalCells = ReadCells(normal, site);
                var probeCells = ReadCells(probe, site);
                var observation = new Observation(site, height, bed, waterLevel, biome,
                    dirtDepth, normal.IsBlockDataReady, probe.IsBlockDataReady,
                    normalCells, probeCells);
                observations.Add(observation);
                output.WriteLine($"S6B_MOSS_PAIR_ACTUAL label={site.Label} "
                    + $"normalReady={observation.NormalReady} probeReady={observation.ProbeReady} "
                    + $"normalFloor={normalCells.Floor}/{Name(normalCells.Floor)}:{normalCells.FloorState} "
                    + $"normalAbove={normalCells.Above}/{Name(normalCells.Above)}:{normalCells.AboveState} "
                    + $"probeFloor={probeCells.Floor}/{Name(probeCells.Floor)}:{probeCells.FloorState} "
                    + $"probeAbove={probeCells.Above}/{Name(probeCells.Above)}:{probeCells.AboveState}");
                sitesLogged++;
            }

            // Record both actual sites before any frozen metadata or material assertion.
            foreach (var observed in observations)
            {
                Assert.Equal(observed.Site.WorldFloorY,
                    observed.Site.Cy * Chunk.Size + observed.Site.LocalY);
                Assert.Equal(observed.Site.WorldFloorY, observed.Height);
                Assert.Equal(observed.Site.WorldFloorY, observed.Bed);
                Assert.Equal(WorldGenerationConfig.SeaLevel, observed.WaterLevel);
                Assert.Equal(Biome.Ocean, observed.Biome);
                Assert.Equal((byte)2, observed.DirtDepth);
                Assert.True(observed.NormalReady && observed.ProbeReady,
                    $"Both {observed.Site.Label} chunks must finish.");
                AssertCells(observed.Normal, observed.Site.ExpectedFloor, water);
                AssertCells(observed.Probe, observed.Site.ExpectedFloor, water);
                Assert.Equal(observed.Normal, observed.Probe);
            }

            Cells ReadCells(Chunk chunk, FrozenSite site)
            {
                directCellGetters++;
                var floor = chunk.GetLocalBlock(site.LocalX, site.LocalY, site.LocalZ);
                directCellGetters++;
                var floorState = chunk.GetLocalState(site.LocalX, site.LocalY, site.LocalZ);
                directCellGetters++;
                var above = chunk.GetLocalBlock(site.LocalX, site.LocalY + 1, site.LocalZ);
                directCellGetters++;
                var aboveState = chunk.GetLocalState(site.LocalX, site.LocalY + 1, site.LocalZ);
                return new Cells(floor, floorState, above, aboveState);
            }
        }
        finally
        {
            try
            {
                output.WriteLine($"S6B_MOSS_PAIR_LEDGER seed=1337 profile=Dynamic "
                    + $"ownColumnGetAttempts={ownColumnGetAttempts}/2 "
                    + $"normalFillAttempts={normalFillAttempts}/2 "
                    + $"probeConstructorAttempts={probeConstructorAttempts}/2 "
                    + $"probeFillAttempts={probeFillAttempts}/2 "
                    + $"directCellGetters={directCellGetters}/16 sitesLogged={sitesLogged}/2");
            }
            finally
            {
                if (applied) WorldSeed.Apply(previousSeed, previousProfile);
            }
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

    /// <summary>Skips before content, seed, cache, fill or block reads by default.</summary>
    public sealed class OptInNaturalMossFactAttribute : FactAttribute
    {
        public OptInNaturalMossFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
                Skip = $"Set {OptInVariable}=1 for the two frozen normal/probe moss floors.";
        }
    }
}
