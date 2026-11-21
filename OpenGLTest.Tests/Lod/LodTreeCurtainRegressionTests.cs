using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Lod;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Lod;

/// <summary>Fixed natural root regression for the old ground-to-leaf curtain recipe.</summary>
[Collection("Worldgen seed")]
public sealed class LodTreeCurtainRegressionTests
{
    private readonly record struct LeafEnvelope(int RootX, int RootZ, int MinX, int MaxX,
        int MinY, int MaxY, int MinZ, int MaxZ);

    [Fact]
    public void EveryLeafVertexBelongsToAFiniteBatchTreeCrownWhileTerrainRemains()
    {
        TestContent.EnsureLoaded();
        var previousSeed = WorldSeed.Value;
        var previousProfile = WorldSeed.Profile;
        var applied = false;
        try
        {
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            applied = true;
            const int rootX = 4004;
            const int rootZ = 811;
            Assert.True(TreeGenerator.TryGetTree(rootX, rootZ, out var rooted, out _));
            Assert.Equal("jungle_giant", rooted.Name);

            const int rx = 62;
            const int rz = 12;
            var spec = LodRegion.Levels[0];
            var x0 = rx * spec.RegionBlocks;
            var z0 = rz * spec.RegionBlocks;
            var x1 = x0 + spec.RegionBlocks;
            var z1 = z0 + spec.RegionBlocks;
            var reach = TreeGenerator.ReachHorizontal;
            var crowns = new List<LeafEnvelope>();
            for (var cx = TreeGenerator.CellOf(x0 - reach); cx <= TreeGenerator.CellOf(x1 - 1 + reach); cx++)
            for (var cz = TreeGenerator.CellOf(z0 - reach); cz <= TreeGenerator.CellOf(z1 - 1 + reach); cz++)
            foreach (var shape in TreeBatches.BatchOf(cx, cz).Trees)
            {
                if (!shape.Species.HasLeaves) continue;
                var minX = int.MaxValue;
                var maxX = int.MinValue;
                var minY = int.MaxValue;
                var maxY = int.MinValue;
                var minZ = int.MaxValue;
                var maxZ = int.MinValue;
                for (var c = shape.WoodCount; c < shape.Cells.Length; c++)
                {
                    var leaf = shape.Cells[c];
                    minX = Math.Min(minX, leaf.X);
                    maxX = Math.Max(maxX, leaf.X + 1);
                    minY = Math.Min(minY, leaf.Y);
                    maxY = Math.Max(maxY, leaf.Y + 1);
                    minZ = Math.Min(minZ, leaf.Z);
                    maxZ = Math.Max(maxZ, leaf.Z + 1);
                }
                if (minY != int.MaxValue)
                    crowns.Add(new LeafEnvelope(shape.X, shape.Z, minX, maxX, minY, maxY, minZ, maxZ));
            }
            Assert.Contains(crowns, crown => crown.RootX == rootX && crown.RootZ == rootZ);

            var mesh = LodRegion.BuildMeshData(0, rx, rz);
            var trees = mesh.Trees.Instances.Where(tree => tree.HasCrown && tree.HasTrunk).ToArray();
            Assert.NotEmpty(trees);
            Assert.Contains(trees, tree => tree.RootX == rootX && tree.RootZ == rootZ);
            Assert.Contains(mesh.Vertices, v => (v.PackedColor >> 24) == 255u);
            Assert.DoesNotContain(mesh.Vertices, v => (v.PackedColor >> 24) == 253u);
            foreach (var tree in trees)
            {
                Assert.Contains(crowns, crown => tree.RootX == crown.RootX && tree.RootZ == crown.RootZ
                    && tree.MinX == crown.MinX && tree.MaxX == crown.MaxX
                    && tree.MinZ == crown.MinZ && tree.MaxZ == crown.MaxZ
                    && tree.CrownBottom == crown.MinY && tree.CrownTop == crown.MaxY);
                Assert.True(tree.CrownBottom > tree.BaseY, "the leaf volume descended to ground");
            }
        }
        finally
        {
            if (applied) WorldSeed.Apply(previousSeed, previousProfile);
        }
    }
}
