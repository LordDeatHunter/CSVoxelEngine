using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Lod;
using VoxelEngine.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen.Ocean;

/// <summary>One opt-in, fixed-site observation of a natural submerged pinnacle and its generated LOD.</summary>
[Collection("Worldgen seed")]
public sealed class OceanPinnacleNaturalDiagnostic(ITestOutputHelper output)
{
    private const string OptInVariable = "VOXEL_S6A_NATURAL_PINNACLE";

    // The canonical seed-1337 first-site witness is frozen here; no other natural slope
    // may substitute for the observed pinnacle when its height application is omitted.
    private static readonly (int Cx, int Cz, int X, int Z, int Peak)[] Sites =
    [
        (-10, -24, -1199, -3069, -12),
    ];

    private static readonly (int Dx, int Dz)[] Stencil =
    [
        (0, 0), (-14, 0), (14, 0), (-28, 0), (28, 0),
        (0, -14), (0, 14), (0, -28), (0, 28),
    ];

    [OptInPinnacleFact]
    public void FrozenNaturalPinnacleMatchesNearFloorAndGeneratedLevelZeroLod()
    {
        var metadataGets = 0;
        var normalFills = 0;
        var lodMeshes = 0;
        var directCellReads = 0;
        var inspectedSites = 0;
        var applied = false;
        var previousSeed = WorldSeed.Value;
        var previousProfile = WorldSeed.Profile;

        try
        {
            TestContent.EnsureLoaded();
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            applied = true;

            foreach (var (siteCx, siteCz, centerX, centerZ, configuredPeak) in Sites)
            {
                inspectedSites++;
                var rows = new Sample[Stencil.Length];
                for (var i = 0; i < Stencil.Length; i++)
                {
                    var (dx, dz) = Stencil[i];
                    var x = centerX + dx;
                    var z = centerZ + dz;
                    metadataGets++;
                    var column = ColumnCache.Get(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
                    var index = ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);
                    rows[i] = new Sample(x, z, column.Biomes[index], column.Heights[index],
                        column.Bed[index], column.WaterLevel[index]);
                    output.WriteLine($"S6a pinnacle site=({siteCx},{siteCz}) configuredPeak={configuredPeak} "
                        + $"point={i} x={x} z={z} biome={rows[i].Biome} bed={rows[i].Bed} "
                        + $"height={rows[i].Height} water={rows[i].Water}");
                }

                var center = rows[0];
                // The -12 floor is the recorded canonical witness, not a substitute-site predicate.
                Assert.Equal(configuredPeak, center.Height);
                if (!SeaFloor(center) || center.Height > WorldGenerationConfig.SeaLevel - 4)
                {
                    output.WriteLine($"S6a site=({siteCx},{siteCz}) miss=center-admission");
                    continue;
                }

                // Require one in-region shoulder and a deep outside-disc control in the same
                // direction. These are observed final heights, not a copied pinnacle formula.
                var direction = -1;
                for (var i = 0; i < 4; i++)
                {
                    var shoulderIndex = i switch { 0 => 1, 1 => 2, 2 => 5, _ => 6 };
                    var exteriorIndex = i switch { 0 => 3, 1 => 4, 2 => 7, _ => 8 };
                    var shoulder = rows[shoulderIndex];
                    var exterior = rows[exteriorIndex];
                    if (!SeaFloor(shoulder) || !DeepSea(exterior)
                        || !SameLevelZeroRegion(center, shoulder)
                        || center.Height < shoulder.Height + 3
                        || center.Height < exterior.Height + 8)
                        continue;
                    direction = i;
                    break;
                }
                if (direction < 0)
                {
                    output.WriteLine($"S6a site=({siteCx},{siteCz}) miss=no-deep-elevated-axis-with-in-region-shoulder");
                    continue;
                }

                var selectedShoulder = rows[direction switch { 0 => 1, 1 => 2, 2 => 5, _ => 6 }];
                var selectedExterior = rows[direction switch { 0 => 3, 1 => 4, 2 => 7, _ => 8 }];
                var chunkX = centerX >> Chunk.SizeShift;
                var chunkZ = centerZ >> Chunk.SizeShift;
                var chunkY = MathUtils.FloorDiv(center.Height, Chunk.Size);
                var localY = center.Height - chunkY * Chunk.Size;
                normalFills++;
                var chunk = new Chunk(null!, new Vector3(chunkX, chunkY, chunkZ), generateBlockData: true);
                Assert.True(chunk.IsBlockDataReady);

                directCellReads += 2;
                var top = chunk.GetLocalBlock(centerX & Chunk.SizeMask, localY, centerZ & Chunk.SizeMask);
                var topState = chunk.GetLocalState(centerX & Chunk.SizeMask, localY, centerZ & Chunk.SizeMask);
                output.WriteLine($"S6a near key=({chunkX},{chunkY},{chunkZ}) floor={center.Height} "
                    + $"top={Name(top)}:{topState} shoulder={selectedShoulder.Height} exterior={selectedExterior.Height}");
                Assert.Contains(top, new[] { TerrainGenerator.SandBlock, TerrainGenerator.GravelBlock,
                    TerrainGenerator.RockBlock });
                Assert.Equal((byte)0, topState);

                if (localY + 1 < Chunk.Size)
                {
                    directCellReads += 2;
                    var above = chunk.GetLocalBlock(centerX & Chunk.SizeMask, localY + 1, centerZ & Chunk.SizeMask);
                    var aboveState = chunk.GetLocalState(centerX & Chunk.SizeMask, localY + 1, centerZ & Chunk.SizeMask);
                    output.WriteLine($"S6a near waterAbove={Name(above)}:{aboveState}");
                    Assert.Equal(TerrainGenerator.SeaBlock, above);
                    Assert.Equal((byte)0, aboveState);
                }
                else
                    output.WriteLine("S6a near waterAbove=outside-one-authorized-Y-chunk");

                var regionBlocks = LodRegion.Levels[0].RegionBlocks;
                var rx = MathUtils.FloorDiv(centerX, regionBlocks);
                var rz = MathUtils.FloorDiv(centerZ, regionBlocks);
                lodMeshes++;
                var mesh = LodRegion.BuildMeshData(0, rx, rz);
                var centerLod = GroundTop(mesh, centerX - rx * regionBlocks, centerZ - rz * regionBlocks);
                var shoulderLod = GroundTop(mesh, selectedShoulder.X - rx * regionBlocks,
                    selectedShoulder.Z - rz * regionBlocks);
                output.WriteLine($"S6a lod region=({rx},{rz}) centerTop={centerLod} "
                    + $"shoulderTop={shoulderLod} centerColumn={center.Height} shoulderColumn={selectedShoulder.Height} "
                    + $"opaqueVertices={mesh.Vertices.Count} waterVertices={mesh.WaterVertices.Count}");
                Assert.True(float.IsFinite(centerLod) && float.IsFinite(shoulderLod));
                Assert.True(centerLod <= center.Height + 1 && shoulderLod <= selectedShoulder.Height + 1,
                    "level-zero eroded terrain top must stay under the corresponding generated columns");
                Assert.True(centerLod > shoulderLod,
                    "one generated LOD region must retain the observed pinnacle above its shoulder");
                output.WriteLine($"S6a WITNESS site=({siteCx},{siteCz}) center=({centerX},{centerZ}) "
                    + $"floor={center.Height} shoulder={selectedShoulder.Height} exterior={selectedExterior.Height} "
                    + $"lodCenter={centerLod} lodShoulder={shoulderLod}");
                return;
            }

            Assert.Fail("The frozen pinnacle did not satisfy its recorded admission/shape; retain the fixed-site ledger.");
        }
        finally
        {
            try
            {
                output.WriteLine($"S6a partial ledger seed=1337 profile=Dynamic sites={inspectedSites}/1 "
                    + $"metadataGets={metadataGets}/9 normalFills={normalFills}/1 "
                    + $"lodMeshes={lodMeshes}/1 directCellReads={directCellReads}/4");
            }
            finally
            {
                if (applied) WorldSeed.Apply(previousSeed, previousProfile);
            }
        }
    }

    private static bool SeaFloor(Sample point) => point.Biome == Biome.Ocean
        && point.Water == WorldGenerationConfig.SeaLevel
        && point.Bed == point.Height;

    private static bool DeepSea(Sample point) => SeaFloor(point)
        && point.Height <= WorldGenerationConfig.SeaLevel - WorldFeatureConfig.OceanPinnacleMinDepth;

    private static bool SameLevelZeroRegion(Sample a, Sample b)
    {
        var blocks = LodRegion.Levels[0].RegionBlocks;
        return MathUtils.FloorDiv(a.X, blocks) == MathUtils.FloorDiv(b.X, blocks)
            && MathUtils.FloorDiv(a.Z, blocks) == MathUtils.FloorDiv(b.Z, blocks);
    }

    private static float GroundTop(LodMeshData mesh, int localX, int localZ)
    {
        var cellSize = LodRegion.Levels[0].CellSize;
        var faceX = localX / cellSize * cellSize;
        var faceZ = localZ / cellSize * cellSize;
        for (var i = 0; i + 3 < mesh.Vertices.Count; i += 4)
        {
            if ((mesh.Vertices[i].Normal & 7) != 4) continue;
            var a = mesh.Vertices[i].Position;
            var b = mesh.Vertices[i + 2].Position;
            if (MathF.Min(a.X, b.X) == faceX && MathF.Min(a.Z, b.Z) == faceZ)
                return a.Y;
        }
        return float.NaN;
    }

    private static string Name(ushort id) => id == 0 ? "air" : BlockType.ById[id]?.Name ?? $"id:{id}";

    private readonly record struct Sample(int X, int Z, Biome Biome, int Height, int Bed, int Water);

    /// <summary>Discovery skips all generation unless this exact frozen observation is requested.</summary>
    public sealed class OptInPinnacleFactAttribute : FactAttribute
    {
        public OptInPinnacleFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
                Skip = $"Set {OptInVariable}=1 for the frozen natural pinnacle/LOD observation.";
        }
    }
}
