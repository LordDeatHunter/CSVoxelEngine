using System;
using System.Numerics;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen.Features;

/// <summary>Real-fill follow-up for two fixed public Taiga fern admissions.</summary>
[Collection("Worldgen seed")]
public sealed class TaigaFernFrozenFillTests(ITestOutputHelper output)
{
    // Frozen from the seed-1337 Dynamic public-admission observation; both straddle an X chunk boundary.
    private static readonly (int X, int Z, int Cx, int Cz, int Lx, int Lz)[] Sites =
    [
        (-15894, 6392, -994, 399, 10, 8),
        (-15888, 6390, -993, 399, 0, 6),
    ];

    [Fact]
    public void FrozenWarmEdgeTaigaFernsReachNormalAndPlannerProbeSurface()
    {
        TestContent.EnsureLoaded();
        var grass = ContentRegistry.RequireBlock("grass").Id;
        var fern = ContentRegistry.RequireBlock("fern").Id;
        var columnAttempts = 0;
        var normalAttempts = 0;
        var probeAttempts = 0;
        var blockReads = 0;
        var stateReads = 0;
        var siteMembers = 0;
        var applied = false;
        try
        {
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            applied = true;
            foreach (var site in Sites)
            {
                Assert.Equal(site.Cx, MathUtils.FloorDiv(site.X, Chunk.Size));
                Assert.Equal(site.Cz, MathUtils.FloorDiv(site.Z, Chunk.Size));
                Assert.Equal(site.Lx, site.X - site.Cx * Chunk.Size);
                Assert.Equal(site.Lz, site.Z - site.Cz * Chunk.Size);
                const int cy = 4;
                const int height = 66;
                const int worldY = height + 1;
                var localY = worldY - cy * Chunk.Size;
                Assert.InRange(localY, 1, Chunk.Size - 2);

                columnAttempts++;
                var column = ColumnCache.Get(site.Cx, site.Cz);
                var ci = (site.Lx << Chunk.SizeShift) | site.Lz;
                siteMembers += 5;
                Assert.Equal(height, column.Heights[ci]);
                Assert.Equal(Biome.Taiga, column.Biomes[ci]);
                Assert.True(column.DirtDepth[ci] > 0);
                Assert.Equal(TerrainGenerator.NoWater, column.WaterLevel[ci]);
                Assert.False(column.Oasis[ci]);
                Assert.Equal(Biome.Taiga, TerrainGenerator.FloraBiomeAt(site.X, site.Z));
                Assert.Equal(0, SnowGenerator.DepthAt(site.X, site.Z, height));
                Assert.False(TerrainGenerator.IsWaterColumn(site.X, site.Z, height));
                Assert.True(SurfaceClearance.Allows(height, 1));
                Assert.True(FoliageGenerator.TryGetGrassTuft(site.X, site.Z));
                Assert.Equal(fern, DynamicGroundCover.CandidateAt(site.X, site.Z, column.Biomes[ci]));
                Assert.True(FoliageGenerator.WriteBand(column).Contains(worldY));

                var key = new Vector3(site.Cx, cy, site.Cz);
                normalAttempts++;
                var normal = new Chunk(null!, key, generateBlockData: true);
                var probe = new Chunk(null!, key, generateBlockData: false);
                probeAttempts++;
                ChunkTerrainFill.GenerateBlockData(probe, generateLakes: false);
                Assert.True(normal.IsBlockDataReady && probe.IsBlockDataReady);

                var actual = Read(normal, site.Lx, localY, site.Lz);
                var planned = Read(probe, site.Lx, localY, site.Lz);
                output.WriteLine($"S4a Taiga fixedsite x={site.X} z={site.Z} key=({site.Cx},{cy},{site.Cz}) local=({site.Lx},{localY},{site.Lz}) stored={column.Biomes[ci]} height={column.Heights[ci]} normalGround={actual.Ground} normalBase={actual.Base} normalAbove={actual.Above} normalGroundState={actual.GroundState} normalBaseState={actual.BaseState} normalAboveState={actual.AboveState} probeGround={planned.Ground} probeBase={planned.Base} probeAbove={planned.Above} probeGroundState={planned.GroundState} probeBaseState={planned.BaseState} probeAboveState={planned.AboveState}");
                Assert.Equal(grass, actual.Ground);
                Assert.Equal(fern, actual.Base);
                Assert.Equal((byte)0, actual.BaseState);
                Assert.Equal(actual.Ground, planned.Ground);
                Assert.Equal(actual.Base, planned.Base);
                Assert.Equal(actual.GroundState, planned.GroundState);
                Assert.Equal(actual.BaseState, planned.BaseState);
            }
        }
        finally
        {
            output.WriteLine($"S4a Taiga partial ledger columnAttempts={columnAttempts}/2 normalAttempts={normalAttempts}/2 plannerProbeAttempts={probeAttempts}/2 siteMembers={siteMembers}/10 blockReads={blockReads}/12 stateReads={stateReads}/12");
            if (applied) WorldSeed.Apply(WorldSeed.Default);
        }

        (ushort Ground, ushort Base, ushort Above, byte GroundState, byte BaseState, byte AboveState)
            Read(Chunk chunk, int x, int y, int z)
        {
            blockReads += 3;
            stateReads += 3;
            return (chunk.GetLocalBlock(x, y - 1, z), chunk.GetLocalBlock(x, y, z),
                chunk.GetLocalBlock(x, y + 1, z), chunk.GetLocalState(x, y - 1, z),
                chunk.GetLocalState(x, y, z), chunk.GetLocalState(x, y + 1, z));
        }
    }
}
