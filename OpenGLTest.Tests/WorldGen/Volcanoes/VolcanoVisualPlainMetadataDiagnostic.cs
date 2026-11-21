using System;
using VoxelEngine.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen.Volcanoes;

/// <summary>Opt-in fixed-column metadata check for the S5a natural rocky-plain view.</summary>
[Collection("Worldgen seed")]
public sealed class VolcanoVisualPlainMetadataDiagnostic(ITestOutputHelper output)
{
    private const string OptInVariable = "VOXEL_S5A_VISUAL_PLAIN_METADATA";
    private const int Seed = 1337;

    // Predetermined east and south offsets from the frozen site; no ring or adaptive search.
    private static readonly (int X, int Z)[] Points =
    [
        (-14966, -7804),
        (-15056, -7714),
        (-14896, -7804),
        (-15056, -7644),
    ];

    [OptInMetadataFact]
    public void FirstDryCharredPlainInTheFourFrozenColumns()
    {
        TestContent.EnsureLoaded();
        var previousSeed = WorldSeed.Value;
        var previousProfile = WorldSeed.Profile;
        var entered = 0;
        var completed = 0;
        var chosen = -1;

        try
        {
            WorldSeed.Apply(Seed, WorldGenProfile.Dynamic);
            for (var i = 0; i < Points.Length; i++)
            {
                var (x, z) = Points[i];
                entered++;
                var cx = x >> Chunk.SizeShift;
                var cz = z >> Chunk.SizeShift;
                var local = ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);
                var column = ColumnCache.Get(cx, cz);
                var biome = column.Biomes[local];
                var height = column.Heights[local];
                var water = column.WaterLevel[local];
                var dirt = column.DirtDepth[local];
                var bed = column.Bed[local];
                completed++;
                output.WriteLine($"S5A_VISUAL_POINT index={i} x={x} z={z} cx={cx} cz={cz} " +
                    $"biome={biome} height={height} bed={bed} water={water} dirt={dirt}");
                if (biome != Biome.Charred || water != TerrainGenerator.NoWater)
                    continue;

                chosen = i;
                break;
            }

            Assert.True(chosen >= 0, "No dry Charred column among the four frozen visual points.");
        }
        finally
        {
            try
            {
                output.WriteLine($"S5A_VISUAL_PLAIN completed={completed}/{entered} " +
                    $"chosenIndex={chosen} cap={Points.Length}");
            }
            finally
            {
                WorldSeed.Apply(previousSeed, previousProfile);
            }
        }
    }

    /// <summary>Default discovery never runs the fixed visual metadata probe.</summary>
    public sealed class OptInMetadataFactAttribute : FactAttribute
    {
        public OptInMetadataFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
                Skip = $"Set {OptInVariable}=1 for the fixed S5a visual metadata probe.";
        }
    }
}
