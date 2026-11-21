using System;
using System.Collections.Generic;
using VoxelEngine.Chunks.Sources;
using VoxelEngine.Data;
using VoxelEngine.Lod;
using VoxelEngine.Tests.Support;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Volcanoes;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen.Volcanoes;

/// <summary>Opt-in, fixed-region LOD check at the accepted natural S5a caldera.</summary>
[Collection("Worldgen seed")]
public sealed class VolcanoFrozenLodSourceDiagnostic(ITestOutputHelper output)
{
    private const string OptInVariable = "VOXEL_S5A_FROZEN_LOD_SOURCE";
    private const int Seed = 1337;
    private const int Level = 0;
    private const int RegionX = -236;
    private const int RegionZ = -122;
    private const int TopFace = 4;
    private const int LocalPatchX = 48;
    private const int LocalPatchZ = 18;
    private const int SuppliedHeight = 73;

    [OptInLodFact]
    public void GeneratedCalderaLavaAndFullySuppliedCharredMetadataStaySeparate()
    {
        TestContent.EnsureLoaded();
        var previousSeed = WorldSeed.Value;
        var previousProfile = WorldSeed.Profile;
        var generatedEntered = 0;
        var generatedCompleted = 0;
        var suppliedEntered = 0;
        var suppliedCompleted = 0;
        var generatedQuads = 0;
        var suppliedQuads = 0;
        CoverageSource? suppliedSource = null;

        try
        {
            WorldSeed.Apply(Seed, WorldGenProfile.Dynamic);
            var spec = LodRegion.Levels[Level];
            var span = spec.RegionBlocks / Chunk.Size;
            Assert.Equal(2, spec.CellSize);
            Assert.Equal(0, spec.HeightBias);
            Assert.Equal(-944, RegionX * span);
            Assert.Equal(-488, RegionZ * span);
            Assert.InRange(-942, RegionX * span, (RegionX + 1) * span - 1);
            Assert.InRange(-941, RegionX * span, (RegionX + 1) * span - 1);
            Assert.Equal(-15056, RegionX * spec.RegionBlocks + LocalPatchX);
            Assert.Equal(-7790, RegionZ * spec.RegionBlocks + LocalPatchZ);

            var lavaTile = BlockType.ById[VolcanoStamp.LavaBlock]!.FaceTiles[TopFace];
            var charredTile = BlockType.ById[TerrainGenerator.CharredStoneBlock]!.FaceTiles[TopFace];

            generatedEntered++;
            var generated = LodRegion.BuildMeshData(Level, RegionX, RegionZ,
                source: GeneratedWorldSource.Instance);
            generatedCompleted++;
            var lavaTop = TopQuadAt(generated, spec, LocalPatchX, LocalPatchZ);
            generatedQuads++;
            Assert.All(lavaTop, v =>
            {
                Assert.Equal(lavaTile, v.Layer);
                Assert.InRange(v.Position.Y, 151 - spec.CellSize, 151);
                Assert.NotEqual((uint)253, v.PackedColor >> 24);
            });

            // The 4x4 region chunk columns plus the one-column prefetch border on each side.
            // CoverageSource rejects a missing coordinate before FlatWorldSource can fall back.
            var cx0 = RegionX * span;
            var cz0 = RegionZ * span;
            var flat = new FlatWorldSource(cx0 - 1, cx0 + span, cz0 - 1, cz0 + span,
                SuppliedHeight, TerrainGenerator.CharredStoneBlock, Biome.Charred,
                warmth: 0.65f, wetness: 0.15f, dirtDepth: 0);
            suppliedSource = new CoverageSource(flat, cx0 - 1, cx0 + span, cz0 - 1, cz0 + span);
            suppliedEntered++;
            var supplied = LodRegion.BuildMeshData(Level, RegionX, RegionZ, source: suppliedSource);
            suppliedCompleted++;
            Assert.Equal((span + 2) * (span + 2), suppliedSource.GetCount);
            Assert.Equal(suppliedSource.GetCount, suppliedSource.ClaimCount);
            Assert.Equal(suppliedSource.GetCount, suppliedSource.DistinctColumns);
            Assert.NotEmpty(supplied.Vertices);
            Assert.Empty(supplied.WaterVertices);
            Assert.DoesNotContain(supplied.Vertices, v => v.Layer == lavaTile);
            Assert.DoesNotContain(supplied.Vertices, v => (v.PackedColor >> 24) == 253);

            var suppliedTop = TopQuadAt(supplied, spec, LocalPatchX, LocalPatchZ);
            suppliedQuads++;
            Assert.All(suppliedTop, v =>
            {
                Assert.Equal(charredTile, v.Layer);
                Assert.NotEqual(lavaTile, v.Layer);
                Assert.InRange(v.Position.Y, SuppliedHeight - spec.CellSize, SuppliedHeight);
            });
        }
        finally
        {
            try
            {
                output.WriteLine($"S5A_FROZEN_LOD generated={generatedCompleted}/{generatedEntered} " +
                    $"generatedPatchQuads={generatedQuads} supplied={suppliedCompleted}/{suppliedEntered} " +
                    $"suppliedPatchQuads={suppliedQuads} suppliedGet={suppliedSource?.GetCount ?? 0} " +
                    $"suppliedClaims={suppliedSource?.ClaimCount ?? 0}");
            }
            finally
            {
                WorldSeed.Apply(previousSeed, previousProfile);
            }
        }
    }

    private static Vertex[] TopQuadAt(LodMeshData mesh, LodLevelSpec spec, int x, int z)
    {
        for (var i = 0; i + 3 < mesh.Vertices.Count; i += 4)
        {
            var a = mesh.Vertices[i];
            var b = mesh.Vertices[i + 1];
            var c = mesh.Vertices[i + 2];
            var d = mesh.Vertices[i + 3];
            if ((a.Normal & 7) != TopFace || (b.Normal & 7) != TopFace ||
                (c.Normal & 7) != TopFace || (d.Normal & 7) != TopFace)
                continue;
            var minX = MathF.Min(MathF.Min(a.Position.X, b.Position.X), MathF.Min(c.Position.X, d.Position.X));
            var maxX = MathF.Max(MathF.Max(a.Position.X, b.Position.X), MathF.Max(c.Position.X, d.Position.X));
            var minZ = MathF.Min(MathF.Min(a.Position.Z, b.Position.Z), MathF.Min(c.Position.Z, d.Position.Z));
            var maxZ = MathF.Max(MathF.Max(a.Position.Z, b.Position.Z), MathF.Max(c.Position.Z, d.Position.Z));
            if (minX == x && maxX == x + spec.CellSize && minZ == z && maxZ == z + spec.CellSize)
                return [a, b, c, d];
        }

        Assert.Fail($"No top quad at local LOD cell ({x},{z}).");
        return [];
    }

    private sealed class CoverageSource(FlatWorldSource source, int cxMin, int cxMax, int czMin, int czMax)
        : IWorldSource
    {
        private readonly HashSet<(int X, int Z)> _columns = [];

        public int GetCount { get; private set; }
        public int ClaimCount { get; private set; }
        public int DistinctColumns => _columns.Count;

        private void RequireCovered(int x, int z)
        {
            Assert.InRange(x, cxMin, cxMax);
            Assert.InRange(z, czMin, czMax);
            Assert.True(source.Supplies(x, z));
        }

        public ColumnData GetColumn(int chunkX, int chunkZ)
        {
            RequireCovered(chunkX, chunkZ);
            GetCount++;
            _columns.Add((chunkX, chunkZ));
            return source.GetColumn(chunkX, chunkZ);
        }

        public bool Supplies(int chunkX, int chunkZ)
        {
            RequireCovered(chunkX, chunkZ);
            ClaimCount++;
            return true;
        }

        public bool IsReady(int chunkX, int chunkZ) => throw new InvalidOperationException("LOD must not probe readiness.");

        public bool TryGet(int chunkX, int chunkZ, out ColumnTerrain terrain) =>
            throw new InvalidOperationException("LOD must not request streamed terrain.");

        public void Fill(Chunk chunk) => throw new InvalidOperationException("LOD must not fill a chunk.");
    }

    /// <summary>Default discovery cannot execute the fixed LOD/source control.</summary>
    public sealed class OptInLodFactAttribute : FactAttribute
    {
        public OptInLodFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
                Skip = $"Set {OptInVariable}=1 for the frozen LOD/source control.";
        }
    }
}
