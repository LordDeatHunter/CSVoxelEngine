using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class ContentTests
{
    public ContentTests() => TestContent.EnsureLoaded();

    [Fact]
    public void BlockAndItemIdsFitInSharedByteRange()
    {
        var ids = BlockType.Types.Keys.Concat(ItemType.Types.Keys).ToArray();
        Assert.NotEmpty(ids);
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.All(ids, id => Assert.NotEqual(BlockType.Air, id));
        Assert.True(ids.Length <= 255);
    }

    [Fact]
    public void WaterIsForcedToFluidBehaviour()
    {
        var water = ContentRegistry.RequireBlock("water");
        Assert.False(water.Solid);
        Assert.True(water.Fluid);
        Assert.True(water.PassesLight);
        Assert.False(water.HasItem);
        Assert.Same(LootTable.None, water.LootTable);
        Assert.True(BlockType.IsFluid(water.Id));
    }

    [Fact]
    public void FullCubeOccludesAfterBake()
    {
        Assert.True(ContentRegistry.RequireBlock("grass").OccludesNeighbours);
    }

    [Fact]
    public void AtlasIsOneSlicePerTextureInFileNameOrder()
    {
        var root = TestContent.FindAssetsRoot();
        var files = System.IO.Directory.GetFiles(System.IO.Path.Combine(root, "textures", "blocks"), "*.png")
            .Select(System.IO.Path.GetFileNameWithoutExtension)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(files.Length, TextureAtlas.LayerCount);
        Assert.Equal(Array.IndexOf(files, "stone"), TextureAtlas.TileOf("stone"));
        Assert.Equal(TextureAtlas.TileOf("grass_top"), ContentRegistry.RequireBlock("grass").FaceTiles[4]);
        Assert.Equal(TextureAtlas.TileOf("grass_side"), ContentRegistry.RequireBlock("grass").FaceTiles[0]);
    }

    [Fact]
    public void BakedQuadsCarryTileLocalUvs()
    {
        // Every face of a full cube spans its whole tile: local UVs sit at 0 or 1 and the quad
        // names the slice it samples, so a merged quad can later run the same UV past 1.
        var stone = ContentRegistry.RequireBlock("stone").BakedModel!;
        Assert.Equal(6, stone.Quads.Length);
        foreach (var quad in stone.Quads)
        {
            Assert.Equal(TextureAtlas.TileOf("stone"), quad.Tile);
            foreach (var uv in quad.Uvs)
            {
                Assert.True(uv.X is 0f or 1f, $"u = {uv.X}");
                Assert.True(uv.Y is 0f or 1f, $"v = {uv.Y}");
            }
        }
    }

    [Fact]
    public void GlassIsTranslucentAndSelfCulling()
    {
        var glass = ContentRegistry.RequireBlock("glass");
        Assert.True(glass.Translucent);
        Assert.True(glass.CullSelf);
        Assert.False(glass.OccludesNeighbours);
        Assert.True(glass.PassesLight);
        Assert.False(glass.CastsAmbientOcclusion);
        Assert.False(BlockType.Occludes(glass.Id));
    }

    [Fact]
    public void StoneAlwaysDropsOneCobblestone()
    {
        var table = ContentRegistry.LootTableFor("stone");
        Assert.NotNull(table);
        var cobble = ContentRegistry.RequireBlock("cobblestone").Id;
        var into = new List<byte>();
        for (var i = 0; i < 10; i++)
        {
            into.Clear();
            table.Roll(new LootContext(0, 0, 0), into);
            Assert.Equal(new[] { cobble }, into);
        }
    }

    [Fact]
    public void NoneLootTableDropsNothing()
    {
        var into = new List<byte>();
        LootTable.None.Roll(new LootContext(0, 0, 0), into);
        Assert.Empty(into);
    }

    [Fact]
    public void UnknownContentThrowsNamingIt()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ContentRegistry.RequireBlock("nope"));
        Assert.Contains("nope", ex.Message);
    }
}
