using System.Collections.Generic;
using VoxelEngine.Chunks;
using VoxelEngine.Lod;
using VoxelEngine.Saves;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Lod;

/// <summary>One frozen natural column checks the live seed/profile cache reset hook.</summary>
[Collection("Worldgen seed")]
public sealed class LodFarRootCacheEpochTests
{
    private const int ChunkX = 250;
    private const int ChunkZ = 50;

    [Fact]
    public void NaturalColumnRebuildsForSameAndChangedWorldRecipes()
    {
        TestContent.EnsureLoaded();
        var previousSeed = WorldSeed.Value;
        var previousProfile = WorldSeed.Profile;
        var previousEdits = LodEditState.Capture();
        var applied = false;
        try
        {
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            applied = true;
            var first = LodFarRootCache.Natural.Column(ChunkX, ChunkZ);
            Assert.Contains(first, root => root.RootX == 4004 && root.RootZ == 811);
            Assert.Equal<LodTreeProxy>(PointOracle(), first);

            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            var sameRecipe = LodFarRootCache.Natural.Column(ChunkX, ChunkZ);
            Assert.NotSame(first, sameRecipe);
            Assert.Equal<LodTreeProxy>(PointOracle(), sameRecipe);

            WorldSeed.Apply(1337, WorldGenProfile.Legacy);
            Assert.Equal<LodTreeProxy>(PointOracle(), LodFarRootCache.Natural.Column(ChunkX, ChunkZ));

            WorldSeed.Apply(1338, WorldGenProfile.Dynamic);
            Assert.Equal<LodTreeProxy>(PointOracle(), LodFarRootCache.Natural.Column(ChunkX, ChunkZ));
        }
        finally
        {
            if (applied)
            {
                WorldSeed.Apply(previousSeed, previousProfile);
                previousEdits.Import();
            }
        }
    }

    private static List<LodTreeProxy> PointOracle()
    {
        var roots = new List<LodTreeProxy>();
        var x0 = ChunkX << Chunk.SizeShift;
        var z0 = ChunkZ << Chunk.SizeShift;
        for (var x = x0; x < x0 + Chunk.Size; x++)
        for (var z = z0; z < z0 + Chunk.Size; z++)
            if (TreeGenerator.TryGetTree(x, z, out var species, out var y) && species.HasLeaves)
                roots.Add(LodTreeProxy.FromFar(species, x, z, y));
        return roots;
    }
}
