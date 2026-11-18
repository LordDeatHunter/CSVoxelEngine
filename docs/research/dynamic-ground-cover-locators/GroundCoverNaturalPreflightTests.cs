using System;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen.Features;

/// <summary>One fixed-window candidate observation before selecting real generated fill sites.</summary>
[Collection("Worldgen seed")]
public sealed class GroundCoverNaturalPreflightTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    // The first three are unverified S4a scenario regions; the fourth is a Taiga
    // column in the frozen EA seed-1344 fixture. Dynamic may classify them differently.
    private static readonly (string Name, int Seed, int Cx, int Cz, Biome Wanted)[] Windows =
    [
        ("forest", 1337, 0, 0, Biome.Forest),
        ("steppe", 1337, 1, 9, Biome.Steppe),
        ("jungle", 1337, 27, 51, Biome.Jungle),
        ("taiga", 1344, 160, 160, Biome.Taiga),
    ];

    public GroundCoverNaturalPreflightTests(ITestOutputHelper output)
    {
        _output = output;
        TestContent.EnsureLoaded();
    }

    public void Dispose() => WorldSeed.Apply(WorldSeed.Default);

    [Fact]
    public void FixedWindowsReportActualDynamicGroundCoverCandidates()
    {
        var fern = ContentRegistry.RequireBlock("fern").Id;
        var tallFern = ContentRegistry.RequireBlock("tall_fern").Id;
        var bush = ContentRegistry.RequireBlock("bush").Id;
        var visits = 0;
        var columns = 0;
        var floraReads = 0;
        var tuftCalls = 0;
        var variantCalls = 0;
        var shrubCalls = 0;
        var waterCalls = 0;
        var snowCalls = 0;
        var found = new bool[Windows.Length];
        var jungleShrubRoots = 0;
        try
        {
            for (var w = 0; w < Windows.Length; w++)
            {
                var window = Windows[w];
                WorldSeed.Apply(window.Seed, WorldGenProfile.Dynamic);
                var column = ColumnCache.Get(window.Cx, window.Cz);
                columns++;
                var matching = 0;
                var tuft = 0;
                var newVariant = 0;
                var dry = 0;
                var snowFree = 0;
                var shrub = 0;
                for (var lx = 0; lx < Chunk.Size; lx++)
                for (var lz = 0; lz < Chunk.Size; lz++)
                {
                    visits++;
                    var wx = window.Cx * Chunk.Size + lx;
                    var wz = window.Cz * Chunk.Size + lz;
                    var ci = (lx << Chunk.SizeShift) | lz;
                    var height = column.Heights[ci];
                    floraReads++;
                    var pureBiome = TerrainGenerator.FloraBiomeAt(wx, wz);
                    if (column.Biomes[ci] != window.Wanted || pureBiome != window.Wanted) continue;
                    matching++;
                    if (window.Name == "jungle")
                    {
                        shrubCalls++;
                        if (JungleBushGenerator.CandidateAt(wx, wz, out _))
                        {
                            shrub++;
                            jungleShrubRoots++;
                            _output.WriteLine($"S4a shrub root seed={window.Seed} x={wx} z={wz} height={height}");
                        }
                    }
                    tuftCalls++;
                    if (!FoliageGenerator.TryGetGrassTuft(wx, wz)) continue;
                    tuft++;
                    variantCalls++;
                    var plant = DynamicGroundCover.CandidateAt(wx, wz, column.Biomes[ci]);
                    var wantedPlant = window.Name == "steppe" ? plant == bush
                        : window.Name == "taiga" ? plant == fern
                        : plant == fern || plant == tallFern || plant == bush;
                    if (!wantedPlant || TerrainGenerator.IsBeachHeight(height)) continue;
                    newVariant++;
                    waterCalls++;
                    if (TerrainGenerator.IsWaterColumn(wx, wz, height)) continue;
                    dry++;
                    if (window.Name == "taiga")
                    {
                        snowCalls++;
                        if (SnowGenerator.DepthAt(wx, wz, height) != 0) continue;
                    }
                    snowFree++;
                    _output.WriteLine($"S4a candidate window={window.Name} seed={window.Seed} x={wx} z={wz} height={height} stored={column.Biomes[ci]} pure={pureBiome} plant={plant}");
                    found[w] = true;
                }
                _output.WriteLine($"S4a window={window.Name} seed={window.Seed} cx={window.Cx} cz={window.Cz} matching={matching} tuft={tuft} newVariant={newVariant} dry={dry} snowFree={snowFree} shrubRoots={shrub}");
            }
        }
        finally
        {
            _output.WriteLine($"S4a direct visits={visits}/1024 columns={columns} floraReads={floraReads} tuftCalls={tuftCalls} variantCalls={variantCalls} shrubCalls={shrubCalls} waterCalls={waterCalls} snowCalls={snowCalls}");
        }
        for (var w = 0; w < Windows.Length; w++)
            Assert.True(found[w], $"{Windows[w].Name} fixed window had no dry new-cover candidate; preserve the miss for disposition");
        Assert.True(jungleShrubRoots > 0, "fixed Jungle window had no pure shrub root; preserve the miss for disposition");
    }
}
