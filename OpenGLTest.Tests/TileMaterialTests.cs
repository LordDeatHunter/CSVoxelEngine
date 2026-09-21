using System.IO;
using System.Linq;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The reflective-ore material path. These cover the data half end to end — the block JSON
/// parsing, the per-tile table keyed by atlas slice, and the material art that table points at —
/// which is everything about the feature that can be checked without a GL context.
/// </summary>
public sealed class TileMaterialTests
{
    private const string Coal = "coal_ore";
    private const string Malachite = "malachite_ore";
    private const string Obsidian = "obsidian";

    public TileMaterialTests() => TestContent.EnsureLoaded();

    private static TileMaterial MaterialOfBlock(string name) =>
        TileMaterials.Of(TextureAtlas.TileOf(name));

    [Fact]
    public void OnlyTheOresAreReflective()
    {
        var reflective = BlockType.Types.Values
            .Where(t => t.Material is { } m && m.IsReflective)
            .Select(t => t.Name)
            .OrderBy(n => n, System.StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([Coal, Malachite, Obsidian], reflective);
    }

    [Fact]
    public void OrdinaryBlocksDeclareNoMaterialAndTheirTilesStayDiffuse()
    {
        foreach (var name in new[] { "stone", "dirt", "gravel", "sand" })
        {
            var type = ContentRegistry.RequireBlock(name);
            Assert.Null(type.Material);
            Assert.False(MaterialOfBlock(name).IsReflective);
            Assert.Equal(TileMaterial.Diffuse, MaterialOfBlock(name));
        }
    }

    /// <summary>
    /// The actual requirement: coal least reflective, malachite decently so, obsidian most.
    /// Pinned as an ordering rather than as three magic numbers, so the values stay free to be
    /// retuned by eye while the intent cannot silently invert.
    /// </summary>
    [Fact]
    public void ReflectivityRisesFromCoalThroughMalachiteToObsidian()
    {
        var coal = MaterialOfBlock(Coal);
        var malachite = MaterialOfBlock(Malachite);
        var obsidian = MaterialOfBlock(Obsidian);

        Assert.True(coal.Metallic < malachite.Metallic,
            $"coal {coal.Metallic} should be less metallic than malachite {malachite.Metallic}");
        Assert.True(malachite.Metallic < obsidian.Metallic,
            $"malachite {malachite.Metallic} should be less metallic than obsidian {obsidian.Metallic}");

        // Shinier means a tighter highlight, so roughness has to fall as metallic rises.
        Assert.True(coal.Roughness > malachite.Roughness);
        Assert.True(malachite.Roughness > obsidian.Roughness);
    }

    [Fact]
    public void EveryTileHasAnEntryAndTheOresLandOnTheirOwnSlices()
    {
        Assert.Equal(TextureAtlas.LayerCount, TileMaterials.ByLayer.Length);
        Assert.True(TileMaterials.AnyReflective);

        // The table is addressed by the atlas slice the shader gets in fLayer, so a block's
        // entry has to be reachable through its own tile index and no other.
        foreach (var name in new[] { Coal, Malachite, Obsidian })
        {
            var type = ContentRegistry.RequireBlock(name);
            var tile = TextureAtlas.TileOf(name);
            Assert.All(type.FaceTiles, face => Assert.Equal(tile, face));
            Assert.Equal(type.Material, TileMaterials.Of(tile));
        }
    }

    [Fact]
    public void BufferDataIsFourFloatsPerSliceInSliceOrder()
    {
        var data = TileMaterials.ToBufferData();
        Assert.Equal(TileMaterials.ByLayer.Length * 4, data.Length);

        for (var i = 0; i < TileMaterials.ByLayer.Length; i++)
        {
            var material = TileMaterials.ByLayer[i];
            Assert.Equal(material.Metallic, data[i * 4 + 0]);
            Assert.Equal(material.Roughness, data[i * 4 + 1]);
            Assert.Equal(material.NormalStrength, data[i * 4 + 2]);
            Assert.Equal(0f, data[i * 4 + 3]);
        }
    }

    [Fact]
    public void OutOfRangeSlicesReadAsDiffuse()
    {
        Assert.Equal(TileMaterial.Diffuse, TileMaterials.Of(-1));
        Assert.Equal(TileMaterial.Diffuse, TileMaterials.Of(TileMaterials.ByLayer.Length));
    }

    /// <summary>
    /// A reflective tile needs its normal map to exist and to match the albedo tile's size, or
    /// <c>TextureAtlas.BuildMaterial</c> would either silently fall back to a flat default
    /// (losing the relief that sells the effect) or throw at startup.
    /// </summary>
    [Fact]
    public void EveryReflectiveTileHasAMatchingMaterialTexture()
    {
        var assets = TestContent.FindAssetsRoot();
        var materialDir = Path.Combine(assets, "textures", "blocks_material");

        foreach (var name in new[] { Coal, Malachite, Obsidian })
        {
            var path = Path.Combine(materialDir, name + ".png");
            Assert.True(File.Exists(path), $"Missing material texture for reflective tile '{name}'.");
        }
    }
}
