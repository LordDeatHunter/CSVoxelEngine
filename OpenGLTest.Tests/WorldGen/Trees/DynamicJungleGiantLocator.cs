using System;
using System.IO;
using VoxelEngine.Chunks;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Trees;

/// <summary>Opt-in, fixed natural-root observation; excluded from ordinary test runs.</summary>
[Collection("Worldgen seed")]
public sealed class DynamicJungleGiantLocator
{
    private const string OptInVariable = "VOXEL_S4B_GIANT_LOCATOR";

    [OptInLocatorFact]
    public void ReportsFirstNaturallyAdmittedGiantInFourFrozenJungleColumns()
    {
        using var log = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
        var columns = new (int X, int Z)[] { (250, 50), (251, 50), (-550, 650), (-549, 650) };
        var attempted = 0;
        var rejected = 0;
        var ordinary = 0;
        var giant = 0;
        var stopped = false;

        try
        {
            TestContent.EnsureLoaded();
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            foreach (var (cx, cz) in columns)
            for (var lx = 0; lx < Chunk.Size; lx++)
            for (var lz = 0; lz < Chunk.Size; lz++)
            {
                var x = cx * Chunk.Size + lx;
                var z = cz * Chunk.Size + lz;
                attempted++;
                var admitted = TreeGenerator.TryGetTree(x, z, out var species, out var baseY);
                var name = admitted ? species.Name : "-";
                if (!admitted) rejected++;
                else if (name == "jungle_giant") giant++;
                else ordinary++;

                log.WriteLine($"S4B_GIANT_SITE index={attempted} cx={cx} cz={cz} lx={lx} lz={lz} x={x} z={z} admitted={admitted} species={name} baseY={baseY}");
                if (giant == 0) continue;
                stopped = true;
                return;
            }

            Assert.True(false, "No naturally admitted jungle_giant in the four frozen columns; retain all 1024 observations as a bounded miss.");
        }
        finally
        {
            log.WriteLine($"S4B_GIANT_SUMMARY attempted={attempted} rejected={rejected} ordinary={ordinary} giant={giant} stopped={stopped} miss={!stopped}");
            WorldSeed.Apply(WorldSeed.Default);
        }
    }

    /// <summary>Discovery skips the diagnostic unless its explicit opt-in is present.</summary>
    public sealed class OptInLocatorFactAttribute : FactAttribute
    {
        public OptInLocatorFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
                Skip = $"Set {OptInVariable}=1 to run the bounded natural-root locator.";
        }
    }
}
