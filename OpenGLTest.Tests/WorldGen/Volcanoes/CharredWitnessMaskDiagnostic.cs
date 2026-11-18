using System;
using System.IO;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Volcanoes;

/// <summary>Opt-in metadata only at previously frozen S4a/S4b Jungle witnesses.</summary>
[Collection("Worldgen seed")]
public sealed class CharredWitnessMaskDiagnostic
{
    private const string OptInVariable = "VOXEL_S5A_WITNESS_MASK";
    private static readonly (string Source, int X, int Z)[] Points =
    [
        ("S4a first shrub", 4013, 813),
        ("S4a second shrub", -8800, 10401),
        ("S4a third shrub", -8799, 10410),
        ("S4a fourth shrub", -8792, 10403),
        ("S4b ordinary", 4002, 802),
        ("S4b giant", 4004, 811),
    ];

    [OptInMaskFact]
    public void ReportsFrozenJungleWitnessProvinceAndBiomeMetadata()
    {
        TestContent.EnsureLoaded();
        using var log = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
        try
        {
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            foreach (var (source, x, z) in Points)
            {
                var classified = BiomeClassifier.BiomeWeights(x, z, out var ocean, out var desert);
                var province = BiomeClassifier.VolcanicProvince(x, z, ocean);
                var spawnMask = TerrainRelief.DynamicSpawnClear(x, z);
                var dithered = BiomeClassifier.DitherBiome(x, z, classified, ocean, desert);
                log.WriteLine($"S5A_WITNESS source={source} seed=1337 profile=Dynamic x={x} z={z} " +
                    $"province={province:R} ocean={ocean:R} desert={desert:R} spawnMask={spawnMask:R} " +
                    $"classified={classified} dithered={dithered}");
            }
        }
        finally { WorldSeed.Apply(WorldSeed.Default); }
    }

    /// <summary>Default discovery cannot execute the diagnostic.</summary>
    public sealed class OptInMaskFactAttribute : FactAttribute
    {
        public OptInMaskFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
                Skip = $"Set {OptInVariable}=1 for the fixed witness metadata report.";
        }
    }
}
