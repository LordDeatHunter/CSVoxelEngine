using System;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Volcanoes;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Volcanoes;

/// <summary>Opt-in, first-hit metadata locator over one frozen Dynamic volcano-cell grid.</summary>
[Collection("Worldgen seed")]
public sealed class VolcanoSiteLocatorDiagnostic
{
    private const string OptInVariable = "VOXEL_S5A_SITE_LOCATOR";
    private const int FirstCell = -8;
    private const int LastCell = 7;

    [OptInLocatorFact]
    public void ReportsFirstRealSiteInFrozenGrid()
    {
        TestContent.EnsureLoaded();
        var previousSeed = WorldSeed.Value;
        var previousProfile = WorldSeed.Profile;
        var attempts = 0;
        var accepted = 0;
        var rejected = 0;
        var found = false;
        var completed = false;

        try
        {
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            for (var gx = FirstCell; gx <= LastCell && !found; gx++)
            for (var gz = FirstCell; gz <= LastCell; gz++)
            {
                attempts++;
                var site = VolcanoSites.SiteAt(gx, gz);
                if (!site.Exists)
                {
                    rejected++;
                    continue;
                }

                accepted++;
                found = true;
                Console.Error.WriteLine($"S5A_SITE source=VolcanoSites.SiteAt seed=1337 profile=Dynamic " +
                    $"gx={gx} gz={gz} x={site.X:R} z={site.Z:R} radius={site.Radius:R} " +
                    $"crater={site.CraterRadius:R} floor={site.Floor} " +
                    $"lakeDepth={site.LakeDepth} rise={site.Rise:R}");
                break;
            }

            completed = true;
        }
        finally
        {
            try
            {
                Console.Error.WriteLine($"S5A_SITE_SUMMARY seed=1337 profile=Dynamic " +
                    $"entries={attempts} accepted={accepted} rejected={rejected} " +
                    $"incomplete={attempts - accepted - rejected} " +
                    $"outcome={(completed ? found ? "hit" : "miss" : "error")}");
            }
            finally
            {
                WorldSeed.Apply(previousSeed, previousProfile);
            }
        }

        Assert.True(found, "No accepted VolcanoSites.SiteAt verdict in the frozen 256-cell grid.");
    }

    /// <summary>Default test discovery skips the expensive natural-site observation.</summary>
    public sealed class OptInLocatorFactAttribute : FactAttribute
    {
        public OptInLocatorFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
                Skip = $"Set {OptInVariable}=1 for the frozen real-site locator.";
        }
    }
}
