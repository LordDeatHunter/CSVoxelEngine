using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Lod;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Lod;

/// <summary>Fixed natural root regression for the old ground-to-leaf curtain recipe.</summary>
[Collection("Worldgen shape cache")]
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
            var leaves = mesh.Vertices.Where(v => (v.PackedColor >> 24) == 253u).ToArray();
            Assert.NotEmpty(leaves);
            Assert.Contains(mesh.Vertices, v => (v.PackedColor >> 24) == 255u);
            foreach (var leaf in leaves)
            {
                var x = leaf.Position.X + x0;
                var z = leaf.Position.Z + z0;
                Assert.Contains(crowns, crown => x >= crown.MinX && x <= crown.MaxX
                    && z >= crown.MinZ && z <= crown.MaxZ
                    && leaf.Position.Y >= crown.MinY && leaf.Position.Y <= crown.MaxY);
            }
        }
        finally
        {
            if (applied) WorldSeed.Apply(previousSeed, previousProfile);
        }
    }
}
