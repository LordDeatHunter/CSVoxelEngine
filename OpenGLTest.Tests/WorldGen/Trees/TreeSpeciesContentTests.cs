using System;
using System.IO;
using System.Linq;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Features;
using VoxelEngine.WorldGen.Trees.Logics;
using VoxelEngine.WorldGen.Trees.Shape;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Trees;

/// <summary>
/// The shipped tree species under <c>assets/data/trees</c>: they load at all, every reference
/// resolves to real content, the registry is keyed by file name, and a broken species file
/// names itself. Iterates the registry rather than naming species.
/// </summary>
public sealed class TreeSpeciesContentTests
{
    public TreeSpeciesContentTests() => TestContent.EnsureLoaded();

    [Fact]
    public void ShippedSpeciesShipAtAll() => Assert.NotEmpty(TreeSpeciesRegistry.All);

    [Fact]
    public void EveryShippedSpeciesResolvesToRealContent()
    {
        foreach (var s in TreeSpeciesRegistry.All)
        {
            Assert.NotNull(BlockType.ById[s.WoodId]);
            Assert.False(BlockType.IsFluid(s.WoodId), s.Name);
            Assert.True(BlockType.IsTreeWood(s.WoodId), s.Name);
            if (s.HasLeaves)
                Assert.NotNull(BlockType.ById[s.LeavesId]);

            Assert.Same(GrowthLogicRegistry.Require(s.Logic.Name), s.Logic);
            foreach (var f in s.Features)
                Assert.Same(TreeFeatureRegistry.Require(f.Feature.Name), f.Feature);

            Assert.True(s.TipPx <= s.MaxPx, s.Name);
            Assert.True(s.MaturityStepsMin <= s.MaturityStepsMax, s.Name);
            Assert.True(s.CrownHeightMin <= s.CrownHeightMax, s.Name);

            if (s.Worldgen is not { } rules) continue;
            foreach (var biome in Enum.GetValues<Biome>())
            {
                var listed = rules.Biomes.Contains(biome);
                Assert.Equal(listed, rules.GrowsIn(biome));
            }
            Assert.All(rules.Biomes, b => Assert.True(Enum.IsDefined(b), s.Name));
        }
    }

    [Fact]
    public void PixelRimFlagFollowsTheSpecies()
    {
        foreach (var s in TreeSpeciesRegistry.All)
            Assert.Equal(s.Rim == TrunkRim.Pixel, BlockType.HasPixelRim(s.WoodId));

        // Nothing but a species' wood ever carries the flag
        var woods = TreeSpeciesRegistry.All.Select(s => s.WoodId).ToHashSet();
        for (var id = 0; id < 256; id++)
            if (!woods.Contains((byte)id))
                Assert.False(BlockType.HasPixelRim((byte)id), BlockType.ById[id]?.Name ?? id.ToString());
    }

    [Fact]
    public void SpeciesWithLeavesHaveAClusterThatFitsTheirTrunk()
    {
        foreach (var s in TreeSpeciesRegistry.All.Where(s => s.HasLeaves))
        {
            Assert.True(s.TipMaxPx <= s.MaxPx, s.Name);
            Assert.InRange(s.LeafHydration, 1, LeafState.MaxHydration);
        }
    }

    [Fact]
    public void RegistryLookupIsByFileNameAndUnknownNamesThrow()
    {
        const string missing = "nope_not_a_species";
        Assert.Null(TreeSpeciesRegistry.TryGet(missing));
        var e = Assert.Throws<InvalidOperationException>(() => TreeSpeciesRegistry.Require(missing));
        Assert.Contains(missing, e.Message);

        foreach (var s in TreeSpeciesRegistry.All)
        {
            Assert.Same(s, TreeSpeciesRegistry.TryGet(s.Name));
            Assert.Same(s, TreeSpeciesRegistry.ByName[s.Name]);
        }
    }

    [Fact]
    public void MalformedSpeciesFileNamesItself()
    {
        var dir = Path.Combine(Path.GetTempPath(), "voxel-tree-species-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            AssertRejectedNamingFile(dir, "broken.json", "{ not json");
            AssertRejectedNamingFile(dir, "unknown_wood.json", ValidSpecies("block/does_not_exist"));
            AssertRejectedNamingFile(dir, "item_wood.json", ValidSpecies("item/flint"));
            // A real block that is not tree wood has no state byte to grow a graph into
            AssertRejectedNamingFile(dir, "plain_wood.json", ValidSpecies("block/stone"));

            // The same body with a real block compiles, so the rejections above are the wood alone.
            var ok = Path.Combine(dir, "ok.json");
            File.WriteAllText(ok, ValidSpecies("block/oak_log"));
            Assert.Equal(ContentRegistry.RequireBlock("oak_log").Id,
                ContentRegistry.CompileTreeSpeciesFile("ok", ok).WoodId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void AssertRejectedNamingFile(string dir, string file, string body)
    {
        var path = Path.Combine(dir, file);
        File.WriteAllText(path, body);
        var e = Assert.Throws<InvalidOperationException>(
            () => ContentRegistry.CompileTreeSpeciesFile(Path.GetFileNameWithoutExtension(file), path));
        Assert.Contains(path, e.Message);
    }

    private static string ValidSpecies(string wood) => $$"""
        {
          "wood": "{{wood}}",
          "growth": { "logic": "{{GrowthLogicRegistry.ByName.Keys.First()}}", "signalEnergy": 4,
                      "upProbability": 1, "lowestBranchHeight": 1, "maturitySteps": [2, 3] },
          "thickness": { "tipPx": 1, "pipeExponent": 2.0, "maxPx": 4 },
          "lod": { "crownHeight": [1, 2], "crownRadius": 1 }
        }
        """;
}
