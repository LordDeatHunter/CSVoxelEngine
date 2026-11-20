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
    [Fact]
    public void EveryLeafVertexBelongsToAFiniteBatchTreeCrownWhileTerrainRemains()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
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
        var crowns = new List<LodTreeProxy>();
        for (var cx = TreeGenerator.CellOf(x0 - reach); cx <= TreeGenerator.CellOf(x1 - 1 + reach); cx++)
        for (var cz = TreeGenerator.CellOf(z0 - reach); cz <= TreeGenerator.CellOf(z1 - 1 + reach); cz++)
        foreach (var shape in TreeBatches.BatchOf(cx, cz).Trees)
        {
            var proxy = LodTreeProxy.FromGrown(shape);
            if (proxy.HasCrown) crowns.Add(proxy);
        }

        var mesh = LodRegion.BuildMeshData(0, rx, rz);
        var leaves = mesh.Vertices.Where(v => (v.PackedColor >> 24) == 253u).ToArray();
        Assert.NotEmpty(leaves);
        Assert.Contains(mesh.Vertices, v => (v.PackedColor >> 24) == 255u);
        foreach (var leaf in leaves)
        {
            var x = leaf.Position.X + x0;
            var z = leaf.Position.Z + z0;
            Assert.Contains(crowns, tree => x >= tree.MinX && x <= tree.MaxX
                && z >= tree.MinZ && z <= tree.MaxZ
                && leaf.Position.Y >= tree.CrownBottom && leaf.Position.Y <= tree.CrownTop);
        }
    }
}
