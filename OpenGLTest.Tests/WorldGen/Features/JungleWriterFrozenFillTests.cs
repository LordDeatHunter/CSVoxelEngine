using System;
using System.Numerics;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen.Features;

/// <summary>Real writer follow-up at the four previously observed snow-free Jungle shrub roots.</summary>
[Collection("Worldgen seed")]
public sealed class JungleWriterFrozenFillTests(ITestOutputHelper output) : IDisposable
{
    private static readonly (int X, int Z, int Height, bool Third)[] FirstRoots =
    [
        (4013, 813, 35, false),
    ];

    private static readonly (int X, int Z, int Height, bool Third)[] SecondRoots =
    [
        (-8800, 10401, 69, true),
        (-8799, 10410, 68, false),
        (-8792, 10403, 70, false),
    ];

    public void Dispose() => WorldSeed.Apply(WorldSeed.Default);

    [Fact]
    public void FrozenJungleRootsReachRealNormalAndPlannerProbeWriters()
    {
        TestContent.EnsureLoaded();
        var fern = ContentRegistry.RequireBlock("fern").Id;
        var tallFern = ContentRegistry.RequireBlock("tall_fern").Id;
        var tallTop = ContentRegistry.RequireBlock("tall_fern_top").Id;
        var bush = ContentRegistry.RequireBlock("bush").Id;
        var grass = ContentRegistry.RequireBlock("grass").Id;
        var jungleMud = ContentRegistry.RequireBlock("jungle_mud").Id;
        var columnAttempts = 0;
        var normalAttempts = 0;
        var probeAttempts = 0;
        var surfaceSites = 0;
        var bandSkipped = 0;
        var blockReads = 0;
        var stateReads = 0;
        var waterLevelReads = 0;
        var rootMembers = 0;
        var cover = 0;
        var tallPairs = 0;
        var completeClusters = 0;
        try
        {
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            InspectColumn(250, 2, 50, FirstRoots);
            InspectColumn(-550, 4, 650, SecondRoots);
            Assert.True(cover > 0, "the two frozen Jungle surfaces produced no real new ground cover");
            Assert.True(tallPairs > 0, "the two frozen Jungle surfaces produced no real named tall-fern pair");
            Assert.True(completeClusters > 0, "none of the four frozen shrub roots became a complete grounded cluster");
        }
        finally
        {
            output.WriteLine($"S4a Jungle writer partial ledger columnAttempts={columnAttempts}/2 normalAttempts={normalAttempts}/2 plannerProbeAttempts={probeAttempts}/2 surfaceSites={surfaceSites}/512 bandSkipped={bandSkipped} rootMembers={rootMembers}/9 blockReads={blockReads}/3126 stateReads={stateReads}/2084 waterLevelReads={waterLevelReads}/512 cover={cover} tallPairs={tallPairs} completeClusters={completeClusters}/4");
            WorldSeed.Apply(WorldSeed.Default);
        }

        void InspectColumn(int cx, int cy, int cz, (int X, int Z, int Height, bool Third)[] roots)
        {
            columnAttempts++;
            var column = ColumnCache.Get(cx, cz);
            var worldX0 = cx * Chunk.Size;
            var worldY0 = cy * Chunk.Size;
            var worldZ0 = cz * Chunk.Size;
            var bushBand = JungleBushGenerator.WriteBand(column);
            var foliageBand = FoliageGenerator.WriteBand(column);
            foreach (var root in roots)
            {
                var lx = root.X - worldX0;
                var lz = root.Z - worldZ0;
                Assert.InRange(lx, 0, Chunk.Size - 2);
                Assert.InRange(lz, 0, root.Third ? Chunk.Size - 2 : Chunk.Size - 1);
                var ci = (lx << Chunk.SizeShift) | lz;
                Assert.Equal(root.Height, column.Heights[ci]);
                Assert.Equal(Biome.Jungle, column.Biomes[ci]);
                Assert.True(bushBand.Contains(root.Height + 1));
                Assert.True(foliageBand.Contains(root.Height + 1));
                Assert.True(foliageBand.Contains(root.Height + 2));
                Assert.InRange(root.Height + 1 - worldY0, 1, Chunk.Size - 2);
            }

            normalAttempts++;
            var normal = new Chunk(null!, new Vector3(cx, cy, cz), generateBlockData: true);
            var probe = new Chunk(null!, new Vector3(cx, cy, cz), generateBlockData: false);
            probeAttempts++;
            ChunkTerrainFill.GenerateBlockData(probe, generateLakes: false);
            Assert.True(normal.IsBlockDataReady && probe.IsBlockDataReady);
            output.WriteLine($"S4a Jungle writer key=({cx},{cy},{cz}) worldY0={worldY0} readyNormal={normal.IsBlockDataReady} readyProbe={probe.IsBlockDataReady} bushBand={bushBand} foliageBand={foliageBand}");

            for (var lx = 0; lx < Chunk.Size; lx++)
            for (var lz = 0; lz < Chunk.Size; lz++)
            {
                surfaceSites++;
                var ci = (lx << Chunk.SizeShift) | lz;
                var height = column.Heights[ci];
                var ly = height + 1 - worldY0;
                if (ly < 1 || ly >= Chunk.Size)
                {
                    bandSkipped++;
                    continue;
                }
                var actual = Read(normal, lx, ly, lz);
                if (actual.Base != fern && actual.Base != tallFern && actual.Base != bush) continue;
                var planned = Read(probe, lx, ly, lz);
                waterLevelReads++;
                var waterLevel = column.WaterLevel[ci];
                var x = worldX0 + lx;
                var z = worldZ0 + lz;
                output.WriteLine($"S4a Jungle cover x={x} z={z} h={height} waterLevel={waterLevel} stored={column.Biomes[ci]} ground={actual.Ground} base={actual.Base} top={actual.Top} state={actual.State} topState={actual.TopState} probeGround={planned.Ground} probeBase={planned.Base} probeTop={planned.Top} probeState={planned.State} probeTopState={planned.TopState}");
                Assert.Equal(Biome.Jungle, column.Biomes[ci]);
                Assert.True(height >= waterLevel, "real Jungle ground cover lies below the column water level");
                Assert.True(actual.Ground == grass || actual.Ground == jungleMud);
                Assert.Equal(actual, planned);
                Assert.True(foliageBand.Contains(height + 1));
                cover++;
                if (actual.Base != tallFern) continue;
                Assert.Equal(tallTop, actual.Top);
                Assert.True(foliageBand.Contains(height + 2));
                tallPairs++;
            }

            foreach (var root in roots)
            {
                var members = root.Third
                    ? new[] { (root.X, root.Z), (root.X + 1, root.Z), (root.X, root.Z + 1) }
                    : new[] { (root.X, root.Z), (root.X + 1, root.Z) };
                var completeNormal = true;
                var completeProbe = true;
                foreach (var (x, z) in members)
                {
                    rootMembers++;
                    var lx = x - worldX0;
                    var lz = z - worldZ0;
                    var ci = (lx << Chunk.SizeShift) | lz;
                    var height = column.Heights[ci];
                    var ly = height + 1 - worldY0;
                    Assert.InRange(ly, 1, Chunk.Size - 2);
                    var actual = Read(normal, lx, ly, lz);
                    var planned = Read(probe, lx, ly, lz);
                    var grounded = height == root.Height && column.Biomes[ci] == Biome.Jungle
                        && (actual.Ground == grass || actual.Ground == jungleMud);
                    completeNormal &= grounded && actual.Base == bush;
                    completeProbe &= height == root.Height && column.Biomes[ci] == Biome.Jungle
                        && (planned.Ground == grass || planned.Ground == jungleMud) && planned.Base == bush;
                    output.WriteLine($"S4a Jungle root=({root.X},{root.Z}) third={root.Third} member=({x},{z}) h={height} stored={column.Biomes[ci]} ground={actual.Ground} base={actual.Base} top={actual.Top} state={actual.State} topState={actual.TopState} probeGround={planned.Ground} probeBase={planned.Base} probeTop={planned.Top} probeState={planned.State} probeTopState={planned.TopState} grounded={grounded}");
                    if (grounded && actual.Base == bush) Assert.Equal(actual, planned);
                }
                output.WriteLine($"S4a Jungle root=({root.X},{root.Z}) completeNormal={completeNormal} completeProbe={completeProbe}");
                if (!completeNormal) continue;
                Assert.True(completeProbe, "a complete normal shrub footprint is incomplete in the planner probe");
                completeClusters++;
            }
        }

        (ushort Ground, ushort Base, ushort Top, byte State, byte TopState) Read(Chunk chunk, int lx, int ly, int lz)
        {
            blockReads += ly + 1 < Chunk.Size ? 3 : 2;
            stateReads += ly + 1 < Chunk.Size ? 2 : 1;
            return (chunk.GetLocalBlock(lx, ly - 1, lz), chunk.GetLocalBlock(lx, ly, lz),
                ly + 1 < Chunk.Size ? chunk.GetLocalBlock(lx, ly + 1, lz) : BlockType.Air,
                chunk.GetLocalState(lx, ly, lz),
                ly + 1 < Chunk.Size ? chunk.GetLocalState(lx, ly + 1, lz) : (byte)0);
        }
    }
}
