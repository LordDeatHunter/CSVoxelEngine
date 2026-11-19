using System;
using System.Linq;
using VoxelEngine.Chunks.Sources;
using VoxelEngine.Lod;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Lod;

/// <summary>Opt-in CPU LOD observations at three frozen generated ocean floor witnesses.</summary>
[Collection("Worldgen seed")]
public sealed class OceanHabitatLodPositiveDiagnostic
{
    private const string OptInVariable = "VOXEL_S6B_POSITIVE_LOD";
    private const int Level = 0;
    private const int TopFace = 4;

    [OptInPositiveLodFact]
    public void GeneratedCoralAndMossEmitSeabedTilesWithoutOverridingSuppliedFloors()
    {
        var previousSeed = WorldSeed.Value;
        var previousProfile = WorldSeed.Profile;
        var applied = false;
        try
        {
            TestContent.EnsureLoaded();
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            applied = true;

            // Frozen near-fill positives. L0 samples two-block cell centers and erodes their
            // height windows, so inspect a bounded neighborhood rather than equate a near voxel
            // with one far voxel. The assertion still reads emitted opaque vertices, not a selector.
            var coral = BuildPositive(-3008, 3911, "coral_amber");
            BuildPositive(-3004, 3535, "sea_moss");
            BuildPositive(-3520, 3659, "glow_moss");

            var span = LodRegion.Levels[Level].RegionBlocks / Chunk.Size;
            var cx = coral.Rx * span;
            var cz = coral.Rz * span;
            var gravelTile = BlockType.ById[TerrainGenerator.GravelBlock]!.FaceTiles[TopFace];
            var supplied = new FlatWorldSource(cx - 1, cx + span, cz - 1, cz + span,
                top: -WorldGenerationConfig.OceanGravelDepth, TerrainGenerator.RockBlock,
                Biome.Ocean, warmth: 0f, wetness: 0f,
                waterLevel: WorldGenerationConfig.SeaLevel);
            var imported = LodRegion.BuildMeshData(Level, coral.Rx, coral.Rz, source: supplied);
            var importedTops = imported.Vertices.Where(IsTop).ToArray();
            Assert.NotEmpty(importedTops);
            Assert.All(importedTops, v => Assert.Equal(gravelTile, v.Layer));
            Assert.NotEmpty(imported.WaterVertices);

            // Supply a neighboring column with opposed stored warmth. The witness cell stays
            // generated; its LOD climate must still come from generated corners. No chunk fill.
            var neighbor = new FlatWorldSource(cx + 1, cx + 1, cz, cz,
                top: -WorldGenerationConfig.OceanGravelDepth, TerrainGenerator.RockBlock,
                Biome.Ocean, warmth: 0f, wetness: 0f,
                waterLevel: WorldGenerationConfig.SeaLevel);
            var mixed = LodRegion.BuildMeshData(Level, coral.Rx, coral.Rz, source: neighbor);
            var generatedPatch = Patch(coral.Mesh, coral.LocalX, coral.LocalZ).ToArray();
            var mixedPatch = Patch(mixed, coral.LocalX, coral.LocalZ).ToArray();
            Assert.NotEmpty(generatedPatch);
            Assert.True(generatedPatch.SequenceEqual(mixedPatch),
                "supplied neighbor climate or surface changed a generated seabed patch");
        }
        finally
        {
            if (applied) WorldSeed.Apply(previousSeed, previousProfile);
        }
    }

    private static (int Rx, int Rz, int LocalX, int LocalZ, LodMeshData Mesh) BuildPositive(
        int worldX, int worldZ, string blockName)
    {
        var regionSize = LodRegion.Levels[Level].RegionBlocks;
        var rx = (int)Math.Floor(worldX / (double)regionSize);
        var rz = (int)Math.Floor(worldZ / (double)regionSize);
        var localX = worldX - rx * regionSize;
        var localZ = worldZ - rz * regionSize;
        var tile = ContentRegistry.RequireBlock(blockName).FaceTiles[TopFace];
        var mesh = LodRegion.BuildMeshData(Level, rx, rz);
        var patch = Patch(mesh, localX, localZ).ToArray();
        Assert.Contains(patch, v => v.Layer == tile);
        Assert.Contains(mesh.WaterVertices, v =>
            Math.Abs(v.Position.X - localX) <= 12 && Math.Abs(v.Position.Z - localZ) <= 12);
        return (rx, rz, localX, localZ, mesh);
    }

    private static bool IsTop(VoxelEngine.Data.Vertex v) => (v.Normal & 7) == TopFace;

    private static System.Collections.Generic.IEnumerable<VoxelEngine.Data.Vertex> Patch(
        LodMeshData mesh, int localX, int localZ) => mesh.Vertices.Where(v =>
            IsTop(v) && Math.Abs(v.Position.X - localX) <= 12
            && Math.Abs(v.Position.Z - localZ) <= 12);

    /// <summary>Discovery skips without initializing worldgen unless the bounded run is selected.</summary>
    public sealed class OptInPositiveLodFactAttribute : FactAttribute
    {
        public OptInPositiveLodFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
                Skip = $"Set {OptInVariable}=1 for three generated and two source L0 builds.";
        }
    }
}
