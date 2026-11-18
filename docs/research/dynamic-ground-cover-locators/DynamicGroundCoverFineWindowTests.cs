using System.Collections.Generic;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen.Features;

/// <summary>Two frozen climate windows for later naturally generated Jungle and Taiga fill controls.</summary>
[Collection("Worldgen seed")]
public sealed class DynamicGroundCoverFineWindowTests(ITestOutputHelper output)
{
    private static readonly (string Name, int Cx, int Cz, Biome Biome)[] Windows =
    [
        ("Jungle", 250, -750, Biome.Jungle),
        ("Taiga", -450, 550, Biome.Taiga),
    ];

    [Fact]
    public void FixedJungleAndWarmTaigaWindowsReportNaturalCandidateMetadata()
    {
        TestContent.EnsureLoaded();
        var fern = ContentRegistry.RequireBlock("fern").Id;
        var tallFern = ContentRegistry.RequireBlock("tall_fern").Id;
        var bush = ContentRegistry.RequireBlock("bush").Id;
        var columnAttempts = 0;
        var sites = 0;
        var floraCalls = 0;
        var tuftCalls = 0;
        var variantCalls = 0;
        var beachCalls = 0;
        var waterCalls = 0;
        var snowCalls = 0;
        var clearanceCalls = 0;
        var shrubCalls = 0;
        var footprintCalls = 0;
        var footprintMembers = 0;
        var jungleCover = 0;
        var taigaFern = 0;
        var junglePureRoots = 0;
        var jungleMetadataRoots = 0;
        try
        {
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            foreach (var window in Windows)
            {
                var reasons = new Dictionary<string, int>();
                var candidates = 0;
                var pureRoots = 0;
                var metadataRoots = 0;
                try
                {
                    columnAttempts++;
                    var column = ColumnCache.Get(window.Cx, window.Cz);
                    for (var lx = 0; lx < Chunk.Size; lx++)
                    for (var lz = 0; lz < Chunk.Size; lz++)
                    {
                        sites++;
                        var x = window.Cx * Chunk.Size + lx;
                        var z = window.Cz * Chunk.Size + lz;
                        var ci = (lx << Chunk.SizeShift) | lz;
                        var height = column.Heights[ci];
                        floraCalls++;
                        var flora = TerrainGenerator.FloraBiomeAt(x, z);

                        if (window.Biome == Biome.Jungle)
                        {
                            shrubCalls++;
                            if (JungleBushGenerator.CandidateAt(x, z, out var third))
                            {
                                output.WriteLine($"S4a pure shrub root x={x} z={z} height={height} stored={column.Biomes[ci]} flora={flora} third={third}");
                                if (flora == Biome.Jungle)
                                {
                                    pureRoots++;
                                    junglePureRoots++;
                                }
                                if (ShrubMetadataFits(column, lx, lz, x, z, height, flora, third))
                                {
                                    metadataRoots++;
                                    jungleMetadataRoots++;
                                    output.WriteLine($"S4a metadata-eligible shrub root x={x} z={z} height={height} third={third}; ground/air/writes unobserved");
                                }
                            }
                        }

                        if (column.Biomes[ci] != window.Biome || flora != window.Biome)
                        {
                            Count("stored-or-flora-mismatch");
                            continue;
                        }
                        tuftCalls++;
                        if (!FoliageGenerator.TryGetGrassTuft(x, z))
                        {
                            Count("no-tuft");
                            continue;
                        }
                        variantCalls++;
                        var variant = DynamicGroundCover.CandidateAt(x, z, column.Biomes[ci]);
                        if (window.Biome == Biome.Jungle
                            ? variant != fern && variant != tallFern && variant != bush
                            : variant != fern)
                        {
                            Count("other-variant");
                            continue;
                        }
                        beachCalls++;
                        if (TerrainGenerator.IsBeachHeight(height))
                        {
                            Count("beach");
                            continue;
                        }
                        waterCalls++;
                        if (TerrainGenerator.IsWaterColumn(x, z, height))
                        {
                            Count("water");
                            continue;
                        }
                        clearanceCalls++;
                        if (!SurfaceClearance.Allows(height, 1))
                        {
                            Count("no-clearance");
                            continue;
                        }
                        var localY = height + 1 - MathUtils.FloorDiv(height + 1, Chunk.Size) * Chunk.Size;
                        if (localY == 0)
                        {
                            Count("ground-outside-y-chunk");
                            continue;
                        }
                        if (window.Biome == Biome.Taiga)
                        {
                            snowCalls++;
                            if (SnowGenerator.DepthAt(x, z, height) != 0)
                            {
                                Count("predicted-snow");
                                continue;
                            }
                            taigaFern++;
                        }
                        else jungleCover++;
                        candidates++;
                        var topFits = true;
                        if (variant == tallFern)
                        {
                            topFits = localY + 1 < Chunk.Size;
                            if (topFits)
                            {
                                clearanceCalls++;
                                topFits = SurfaceClearance.Allows(height, 2);
                            }
                        }
                        output.WriteLine($"S4a metadata cover window={window.Name} x={x} z={z} height={height} stored={column.Biomes[ci]} flora={flora} variant={variant} predictedTopFits={topFits}; soil/air/writes unobserved");
                    }
                }
                finally
                {
                    output.WriteLine($"S4a window={window.Name} candidates={candidates} pureShrubRoots={pureRoots} metadataShrubRoots={metadataRoots}");
                    foreach (var reason in reasons)
                        output.WriteLine($"S4a window={window.Name} rejected-{reason.Key}={reason.Value}");
                }

                void Count(string reason) => reasons[reason] = reasons.GetValueOrDefault(reason) + 1;

                bool ShrubMetadataFits(ColumnData column, int lx, int lz, int x, int z, int height,
                    Biome flora, bool third)
                {
                    // Reject at the column edge before reading any neighbor's metadata.
                    if (lx + 1 >= Chunk.Size || third && lz + 1 >= Chunk.Size)
                    {
                        Count("shrub-cross-column");
                        return false;
                    }
                    footprintCalls++;
                    if (TerrainErg.Intersects(x, z, x + 1, z + (third ? 1 : 0)))
                    {
                        Count("shrub-core");
                        return false;
                    }
                    beachCalls++;
                    if (TerrainGenerator.IsBeachHeight(height))
                    {
                        Count("shrub-beach");
                        return false;
                    }
                    clearanceCalls++;
                    if (!SurfaceClearance.Allows(height, 1)
                        || height + 1 - MathUtils.FloorDiv(height + 1, Chunk.Size) * Chunk.Size == 0)
                    {
                        Count("shrub-surface-or-band");
                        return false;
                    }
                    var rootBiome = column.Biomes[(lx << Chunk.SizeShift) | lz];
                    if (rootBiome != Biome.Jungle || flora != Biome.Jungle)
                    {
                        Count("shrub-root-biome");
                        return false;
                    }
                    if (!Member(lx, lz, x, z) || !Member(lx + 1, lz, x + 1, z)
                        || third && !Member(lx, lz + 1, x, z + 1)) return false;
                    return true;

                    bool Member(int mx, int mz, int wx, int wz)
                    {
                        footprintMembers++;
                        var mi = (mx << Chunk.SizeShift) | mz;
                        if (column.Heights[mi] != height || column.Biomes[mi] != rootBiome)
                        {
                            Count("shrub-member-height-or-biome");
                            return false;
                        }
                        waterCalls++;
                        if (!TerrainGenerator.IsWaterColumn(wx, wz, height)) return true;
                        Count("shrub-member-water");
                        return false;
                    }
                }
            }
            Assert.True(jungleCover > 0, "fixed Jungle column has no predicted new cover; no wider search authorized");
            Assert.True(taigaFern > 0, "fixed Taiga column has no predicted snow-free fern; no wider search authorized");
            Assert.True(junglePureRoots > 0 && jungleMetadataRoots > 0,
                "fixed Jungle column has no pure and metadata-eligible shrub root; no wider search authorized");
        }
        finally
        {
            output.WriteLine($"S4a fine partial ledger columnAttempts={columnAttempts}/2 sites={sites}/512 floraCalls={floraCalls} tuftCalls={tuftCalls} variantCalls={variantCalls} beachCalls={beachCalls} waterCalls={waterCalls} snowCalls={snowCalls} clearanceCalls={clearanceCalls} shrubCalls={shrubCalls}/256 footprintCalls={footprintCalls} footprintMembers={footprintMembers}");
            output.WriteLine($"S4a fine totals jungleCover={jungleCover} taigaSnowFreeFern={taigaFern} junglePureShrubRoots={junglePureRoots} jungleMetadataShrubRoots={jungleMetadataRoots}");
            WorldSeed.Apply(WorldSeed.Default);
        }
    }
}
