using System.Collections.Generic;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen.Features;

/// <summary>Two frozen snow-free Jungle columns for later real ground-cover fill controls.</summary>
[Collection("Worldgen seed")]
public sealed class DynamicJungleSnowFreePreflightTests(ITestOutputHelper output)
{
    private static readonly (int Cx, int Cz)[] Columns = [(250, 50), (-550, 650)];

    [Fact]
    public void FixedJungleColumnsReportSnowFreeCoverAndWholeShrubMetadata()
    {
        TestContent.EnsureLoaded();
        var fern = ContentRegistry.RequireBlock("fern").Id;
        var tallFern = ContentRegistry.RequireBlock("tall_fern").Id;
        var bush = ContentRegistry.RequireBlock("bush").Id;
        var columnAttempts = 0;
        var sites = 0;
        var indexReads = 0;
        var heightReads = 0;
        var storedBiomeReads = 0;
        var floraCalls = 0;
        var rootSelectorCalls = 0;
        var memberChecks = 0;
        var footprintCoreCalls = 0;
        var cellCoreCalls = 0;
        var beachCalls = 0;
        var waterCalls = 0;
        var snowCalls = 0;
        var clearanceCalls = 0;
        var floorDivCalls = 0;
        var tuftCalls = 0;
        var variantCalls = 0;
        var coverCandidates = 0;
        var pureRoots = 0;
        var wholeMetadataRoots = 0;
        var allReasons = new Dictionary<string, int>();
        try
        {
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            foreach (var (cx, cz) in Columns)
            {
                var windowCover = 0;
                var windowPureRoots = 0;
                var windowWholeRoots = 0;
                var reasons = new Dictionary<string, int>();
                try
                {
                    columnAttempts++;
                    var column = ColumnCache.Get(cx, cz);
                    for (var lx = 0; lx < Chunk.Size; lx++)
                    for (var lz = 0; lz < Chunk.Size; lz++)
                    {
                        sites++;
                        var x = cx * Chunk.Size + lx;
                        var z = cz * Chunk.Size + lz;
                        indexReads++;
                        var ci = (lx << Chunk.SizeShift) | lz;
                        heightReads++;
                        var height = column.Heights[ci];
                        storedBiomeReads++;
                        var stored = column.Biomes[ci];
                        floraCalls++;
                        var flora = TerrainGenerator.FloraBiomeAt(x, z);

                        rootSelectorCalls++;
                        if (JungleBushGenerator.CandidateAt(x, z, out var third))
                        {
                            pureRoots++;
                            windowPureRoots++;
                            output.WriteLine($"S4a Jungle pure shrub root x={x} z={z} height={height} stored={stored} flora={flora} third={third}");
                            if (WholeShrubFits(column, lx, lz, x, z, height, stored, third, reasons))
                            {
                                wholeMetadataRoots++;
                                windowWholeRoots++;
                                output.WriteLine($"S4a Jungle snow-free metadata shrub root x={x} z={z} height={height} third={third}; soil/air/writes unobserved");
                            }
                        }

                        if (stored != Biome.Jungle || flora != Biome.Jungle)
                        {
                            Count(reasons, "cover-biome");
                            continue;
                        }
                        tuftCalls++;
                        if (!FoliageGenerator.TryGetGrassTuft(x, z))
                        {
                            Count(reasons, "cover-no-tuft");
                            continue;
                        }
                        variantCalls++;
                        var variant = DynamicGroundCover.CandidateAt(x, z, stored);
                        if (variant != fern && variant != tallFern && variant != bush)
                        {
                            Count(reasons, "cover-other-variant");
                            continue;
                        }

                        cellCoreCalls++;
                        var core = TerrainErg.Intersects(x, z, x, z);
                        beachCalls++;
                        var beach = TerrainGenerator.IsBeachHeight(height);
                        waterCalls++;
                        var water = TerrainGenerator.IsWaterColumn(x, z, height);
                        snowCalls++;
                        var snow = SnowGenerator.DepthAt(x, z, height);
                        clearanceCalls++;
                        var clearance = SurfaceClearance.Allows(height, 1);
                        floorDivCalls++;
                        var localY = height + 1 - MathUtils.FloorDiv(height + 1, Chunk.Size) * Chunk.Size;
                        var topFits = variant != tallFern || localY + 1 < Chunk.Size;
                        if (variant == tallFern && topFits)
                        {
                            clearanceCalls++;
                            topFits = SurfaceClearance.Allows(height, 2);
                        }
                        var predicted = variant == tallFern && !topFits ? fern : variant;
                        var reason = core ? "cover-core" : beach ? "cover-beach" : water ? "cover-water"
                            : snow != 0 ? "cover-snow" : !clearance ? "cover-clearance"
                            : localY == 0 ? "cover-ground-band" : "cover-accepted";
                        Count(reasons, reason);
                        if (reason == "cover-accepted")
                        {
                            coverCandidates++;
                            windowCover++;
                        }
                        output.WriteLine($"S4a Jungle cover x={x} z={z} height={height} stored={stored} flora={flora} variant={variant} predictedPlant={predicted} topFits={topFits} snowDepth={snow} core={core} beach={beach} water={water} clearance={clearance} localY={localY} result={reason}; soil/air/writes unobserved");
                    }
                }
                finally
                {
                    output.WriteLine($"S4a Jungle column cx={cx} cz={cz} acceptedCover={windowCover} pureShrubRoots={windowPureRoots} snowFreeWholeMetadataRoots={windowWholeRoots}");
                    foreach (var reason in reasons)
                        output.WriteLine($"S4a Jungle column cx={cx} cz={cz} reason-{reason.Key}={reason.Value}");
                }
            }
            Assert.True(coverCandidates > 0, "both frozen Jungle columns lack snow-free new-cover metadata; no wider search authorized");
            Assert.True(pureRoots > 0 && wholeMetadataRoots > 0,
                "both frozen Jungle columns lack a pure and snow-free whole-shrub metadata root; no wider search authorized");
        }
        finally
        {
            foreach (var reason in allReasons)
                output.WriteLine($"S4a Jungle combined reason-{reason.Key}={reason.Value}");
            output.WriteLine($"S4a Jungle partial ledger columnAttempts={columnAttempts}/2 sites={sites}/512 indexReads={indexReads}/2048 heightReads={heightReads}/2048 storedBiomeReads={storedBiomeReads}/2048 floraCalls={floraCalls}/2048 rootSelectorCalls={rootSelectorCalls}/512 memberChecks={memberChecks}/1536 footprintCoreCalls={footprintCoreCalls}/512 cellCoreCalls={cellCoreCalls}/2048 beachCalls={beachCalls}/2048 waterCalls={waterCalls}/2048 snowCalls={snowCalls}/2048 clearanceCalls={clearanceCalls}/2560 floorDivCalls={floorDivCalls}/2048 tuftCalls={tuftCalls}/512 variantCalls={variantCalls}/512");
            output.WriteLine($"S4a Jungle totals acceptedCover={coverCandidates} pureShrubRoots={pureRoots} snowFreeWholeMetadataRoots={wholeMetadataRoots}");
            WorldSeed.Apply(WorldSeed.Default);
        }

        void Count(Dictionary<string, int> reasons, string reason)
        {
            reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
            allReasons[reason] = allReasons.GetValueOrDefault(reason) + 1;
        }

        bool WholeShrubFits(ColumnData column, int lx, int lz, int x, int z, int height,
            Biome stored, bool third, Dictionary<string, int> reasons)
        {
            if (lx + 1 >= Chunk.Size || third && lz + 1 >= Chunk.Size)
            {
                Count(reasons, "shrub-cross-column");
                return false;
            }
            footprintCoreCalls++;
            var footprintCore = TerrainErg.Intersects(x, z, x + 1, z + (third ? 1 : 0));
            var first = Member(lx, lz, x, z);
            var second = Member(lx + 1, lz, x + 1, z);
            var final = !third || Member(lx, lz + 1, x, z + 1);
            var accepted = !footprintCore && stored == Biome.Jungle && first && second && final;
            Count(reasons, accepted ? "shrub-accepted" : footprintCore ? "shrub-core" : "shrub-member");
            return accepted;

            bool Member(int mx, int mz, int wx, int wz)
            {
                memberChecks++;
                indexReads++;
                var mi = (mx << Chunk.SizeShift) | mz;
                heightReads++;
                var memberHeight = column.Heights[mi];
                storedBiomeReads++;
                var memberBiome = column.Biomes[mi];
                floraCalls++;
                var memberFlora = TerrainGenerator.FloraBiomeAt(wx, wz);
                snowCalls++;
                var snow = SnowGenerator.DepthAt(wx, wz, memberHeight);
                cellCoreCalls++;
                var core = TerrainErg.Intersects(wx, wz, wx, wz);
                beachCalls++;
                var beach = TerrainGenerator.IsBeachHeight(memberHeight);
                waterCalls++;
                var water = TerrainGenerator.IsWaterColumn(wx, wz, memberHeight);
                clearanceCalls++;
                var clearance = SurfaceClearance.Allows(memberHeight, 1);
                floorDivCalls++;
                var localY = memberHeight + 1 - MathUtils.FloorDiv(memberHeight + 1, Chunk.Size) * Chunk.Size;
                var accepted = memberHeight == height && memberBiome == stored && memberFlora == Biome.Jungle
                    && snow == 0 && !core && !beach && !water && clearance && localY != 0;
                output.WriteLine($"S4a Jungle shrub member root=({x},{z}) member=({wx},{wz}) height={memberHeight} stored={memberBiome} flora={memberFlora} snowDepth={snow} core={core} beach={beach} water={water} clearance={clearance} localY={localY} accepted={accepted}; soil/air/writes unobserved");
                if (!accepted) Count(reasons, "shrub-member-ineligible");
                return accepted;
            }
        }
    }
}
