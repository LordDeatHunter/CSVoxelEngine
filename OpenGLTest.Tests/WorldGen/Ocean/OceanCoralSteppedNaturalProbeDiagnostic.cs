using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content;
using VoxelEngine.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen.Ocean;

/// <summary>Opt-in real normal/probe observation of one frozen stepped-reef candidate.</summary>
[Collection("Worldgen seed")]
public sealed class OceanCoralSteppedNaturalProbeDiagnostic(ITestOutputHelper output)
{
    private const string OptInVariable = "VOXEL_S6B_NATURAL_STEPPED_MOUND";
    private const int Cx = -164, Cy = -1, Cz = 236;
    private const int WorldX0 = -2624, WorldY0 = -16, WorldZ0 = 3776;

    private readonly record struct Cell(string Label, int X, int Y, int Z, ushort Expected);
    private readonly record struct Value(ushort Id, byte State);
    private readonly record struct Metadata(int Height, int Bed, int Water, Biome Biome, byte DirtDepth);

    [OptInNaturalSteppedMoundFact]
    public void FrozenSteppedCoralMoundMatchesNormalAndPlannerProbe()
    {
        var previousSeed = WorldSeed.Value;
        var previousProfile = WorldSeed.Profile;
        var applied = false;
        var ownColumnGets = 0;
        var normalFillAttempts = 0;
        var probeConstructorAttempts = 0;
        var probeFillAttempts = 0;
        var directCellGetters = 0;
        var metadataRowsLogged = 0;
        var actualRowsLogged = 0;

        try
        {
            TestContent.EnsureLoaded();
            var amber = ContentRegistry.RequireBlock("coral_amber").Id;
            var blue = ContentRegistry.RequireBlock("coral_blue").Id;
            var water = ContentRegistry.RequireBlock("water").Id;
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            applied = true;

            ownColumnGets++; // Count before a cold/evicted factory enters.
            var column = ColumnCache.Get(Cx, Cz);
            Cell[] cells =
            [
                // Five actual supports, including the raised east floor.
                new("support-center", 1, 3, 10, amber),
                new("support-west", 0, 3, 10, amber),
                new("support-east", 2, 4, 10, amber),
                new("support-north", 1, 3, 9, amber),
                new("support-south", 1, 3, 11, amber),
                // Seven connected colored targets; the raised east target joins spine Y5.
                new("target-center-low", 1, 4, 10, blue),
                new("target-center-middle", 1, 5, 10, blue),
                new("target-center-top", 1, 6, 10, blue),
                new("target-west", 0, 4, 10, blue),
                new("target-east", 2, 5, 10, blue),
                new("target-north", 1, 4, 9, blue),
                new("target-south", 1, 4, 11, blue),
                // One own-column water cap over each component column; west's outside
                // horizontal neighbor is deliberately unobserved under this one-chunk grant.
                new("cap-center", 1, 7, 10, water),
                new("cap-west", 0, 5, 10, water),
                new("cap-east", 2, 6, 10, water),
                new("cap-north", 1, 5, 9, water),
                new("cap-south", 1, 5, 11, water),
            ];
            int[] supportHeights = [-13, -13, -12, -13, -13];
            var metadata = new Metadata[5];
            for (var i = 0; i < metadata.Length; i++)
            {
                var site = cells[i];
                var ci = (site.X << Chunk.SizeShift) | site.Z;
                metadata[i] = new Metadata(column.Heights[ci], column.Bed[ci],
                    column.WaterLevel[ci], column.Biomes[ci], column.DirtDepth[ci]);
                output.WriteLine($"S6B_STEP_METADATA site={site.Label} "
                    + $"world=({WorldX0 + site.X},{supportHeights[i]},{WorldZ0 + site.Z}) "
                    + $"key=({Cx},{Cy},{Cz}) local=({site.X},{site.Y},{site.Z}) "
                    + $"h={metadata[i].Height} bed={metadata[i].Bed} "
                    + $"water={metadata[i].Water} biome={metadata[i].Biome} "
                    + $"dirtDepth={metadata[i].DirtDepth}");
                metadataRowsLogged++;
            }

            var key = new Vector3(Cx, Cy, Cz);
            normalFillAttempts++; // Constructor performs the real normal fill.
            var normal = new Chunk(null!, key, generateBlockData: true);
            probeConstructorAttempts++;
            var probe = new Chunk(null!, key, generateBlockData: false);
            probeFillAttempts++; // Real probe pipeline, omitting cave-lake planning.
            ChunkTerrainFill.GenerateBlockData(probe, generateLakes: false);

            var normalReady = normal.IsBlockDataReady;
            var probeReady = probe.IsBlockDataReady;
            var normalValues = ReadCells(normal, cells);
            var probeValues = ReadCells(probe, cells);
            output.WriteLine($"S6B_STEP_READY normal={normalReady} probe={probeReady}");
            for (var i = 0; i < cells.Length; i++)
            {
                var site = cells[i];
                var a = normalValues[i];
                var b = probeValues[i];
                output.WriteLine($"S6B_STEP_ACTUAL site={site.Label} "
                    + $"world=({WorldX0 + site.X},{WorldY0 + site.Y},{WorldZ0 + site.Z}) "
                    + $"normal={a.Id}/{Name(a.Id)}:{a.State} "
                    + $"probe={b.Id}/{Name(b.Id)}:{b.State}");
                actualRowsLogged++;
            }

            // Both complete actual paths are logged before any frozen metadata/material assertion.
            Assert.True(normalReady && probeReady);
            Assert.Equal(17, new HashSet<(int X, int Y, int Z)>(
                Array.ConvertAll(cells, c => (c.X, c.Y, c.Z))).Count);
            for (var i = 0; i < cells.Length; i++)
            {
                var site = cells[i];
                Assert.InRange(site.X, 0, Chunk.Size - 1);
                Assert.InRange(site.Y, 0, Chunk.Size - 1);
                Assert.InRange(site.Z, 0, Chunk.Size - 1);
                if (i < 5)
                {
                    Assert.Equal(supportHeights[i], WorldY0 + site.Y);
                    Assert.Equal(supportHeights[i], metadata[i].Height);
                    Assert.Equal(supportHeights[i], metadata[i].Bed);
                    Assert.Equal(WorldGenerationConfig.SeaLevel, metadata[i].Water);
                    Assert.Equal(Biome.Ocean, metadata[i].Biome);
                    Assert.Equal((byte)3, metadata[i].DirtDepth);
                }
                if (i is >= 5 and < 12)
                {
                    Assert.InRange(site.Y, 4, 6);
                    Assert.InRange(Math.Abs(site.X - 1) + Math.Abs(site.Z - 10), 0, 1);
                }
                Assert.Equal(site.Expected, normalValues[i].Id);
                Assert.Equal((byte)0, normalValues[i].State);
                Assert.Equal(site.Expected, probeValues[i].Id);
                Assert.Equal((byte)0, probeValues[i].State);
                Assert.Equal(normalValues[i], probeValues[i]);
            }
            // Independent six-face connections for the fixed seven-cell shape.
            Assert.Equal(1, Distance(cells[5], cells[6]));
            Assert.Equal(1, Distance(cells[6], cells[7]));
            Assert.Equal(1, Distance(cells[5], cells[8]));
            Assert.Equal(1, Distance(cells[6], cells[9]));
            Assert.Equal(1, Distance(cells[5], cells[10]));
            Assert.Equal(1, Distance(cells[5], cells[11]));
        }
        finally
        {
            try
            {
                output.WriteLine($"S6B_STEP_LEDGER seed=1337 profile=Dynamic key=({Cx},{Cy},{Cz}) "
                    + $"ownColumnGets={ownColumnGets}/1 normalFillAttempts={normalFillAttempts}/1 "
                    + $"probeConstructorAttempts={probeConstructorAttempts}/1 "
                    + $"probeFillAttempts={probeFillAttempts}/1 "
                    + $"directCellGetters={directCellGetters}/68 "
                    + $"metadataRowsLogged={metadataRowsLogged}/5 actualRowsLogged={actualRowsLogged}/17");
            }
            finally
            {
                if (applied) WorldSeed.Apply(previousSeed, previousProfile);
            }
        }

        Value[] ReadCells(Chunk chunk, Cell[] sites)
        {
            var result = new Value[sites.Length];
            for (var i = 0; i < sites.Length; i++)
            {
                var site = sites[i];
                directCellGetters++;
                var id = chunk.GetLocalBlock(site.X, site.Y, site.Z);
                directCellGetters++;
                var state = chunk.GetLocalState(site.X, site.Y, site.Z);
                result[i] = new Value(id, state);
            }
            return result;
        }
    }

    private static int Distance(Cell a, Cell b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) + Math.Abs(a.Z - b.Z);

    private static string Name(ushort id) => id == 0 ? "air" : BlockType.ById[id]?.Name ?? $"id:{id}";

    /// <summary>Skips before content, seed, column, fill or block work by default.</summary>
    public sealed class OptInNaturalSteppedMoundFactAttribute : FactAttribute
    {
        public OptInNaturalSteppedMoundFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
                Skip = $"Set {OptInVariable}=1 for the one frozen stepped-mound normal/probe observation.";
        }
    }
}
