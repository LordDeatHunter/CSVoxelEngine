using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StbImageSharp;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The textureless tiles a biome tint draws: every tinted tile is pure grey at the one grey
/// <see cref="BiomeTint.Gain"/> is sized against, a cutout block's tinted tile keeps its source's
/// holes texel for texel, and the default coloured tiles stay with icons and drops.
/// </summary>
public sealed class BiomeTintTextureTests
{
    public BiomeTintTextureTests() => TestContent.EnsureLoaded();

    private static IEnumerable<BlockType> TintedBlocks =>
        BlockType.ById.Where(b => b?.BiomeTint != null).Select(b => b!);

    private static string TilePath(string name) =>
        Path.Combine(TestContent.FindAssetsRoot(), "textures", "blocks", name + ".png");

    private static ImageResult Load(string path)
    {
        using var stream = File.OpenRead(path);
        return ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
    }

    [Fact]
    public void EveryTintedTileIsGreyAtTheGainsGrey()
    {
        var tiles = TintedBlocks
            .SelectMany(b => Enumerable.Range(0, 6).Where(b.BiomeTint!.IsTinted).Select(f => b.BiomeTint!.Tinted[f]))
            .Distinct()
            .ToList();
        Assert.NotEmpty(tiles);

        var opaqueAlpha = WorldGenerationConfig.AlphaCutoff * 255f;
        foreach (var tile in tiles)
        {
            var name = TextureAtlas.TileNames[tile];
            var image = Load(TilePath(name));
            var data = image.Data;
            double sum = 0;
            var opaque = 0;
            for (var i = 0; i < data.Length; i += 4)
            {
                Assert.True(data[i] == data[i + 1] && data[i + 1] == data[i + 2],
                    $"tinted tile '{name}' texel {i / 4} is coloured ({data[i]}, {data[i + 1]}, {data[i + 2]})");
                if (data[i + 3] < opaqueAlpha) continue;
                sum += data[i];
                opaque++;
            }

            Assert.True(opaque > 0, $"tinted tile '{name}' has no opaque texel");
            var mean = sum / opaque / 255.0;
            Assert.True(Math.Abs(mean - WorldGenerationConfig.BiomeTintTextureGrey) <= 2.0 / 255.0,
                $"tinted tile '{name}' averages grey {mean * 255:F1}, not the gain's {WorldGenerationConfig.BiomeTintTextureGrey * 255:F1}");
        }
    }

    [Fact]
    public void ACutoutBlocksTintedTileKeepsItsSourceHoles()
    {
        const string suffix = "_textureless";
        var compared = 0;
        foreach (var block in TintedBlocks.Where(b => b.Cutout))
        {
            var bt = block.BiomeTint!;
            foreach (var tile in Enumerable.Range(0, 6).Where(bt.IsTinted).Select(f => bt.Tinted[f]).Distinct())
            {
                var name = TextureAtlas.TileNames[tile];
                if (!name.EndsWith(suffix, StringComparison.Ordinal)) continue;
                var sourcePath = TilePath(name[..^suffix.Length]);
                if (!File.Exists(sourcePath)) continue;

                var tinted = Load(TilePath(name));
                var source = Load(sourcePath);
                Assert.Equal(source.Width, tinted.Width);
                Assert.Equal(source.Height, tinted.Height);
                for (var i = 3; i < source.Data.Length; i += 4)
                    Assert.True(source.Data[i] == tinted.Data[i],
                        $"'{name}' texel {i / 4} has alpha {tinted.Data[i]}, its source {source.Data[i]}");
                compared++;
            }
        }

        Assert.True(compared > 0, "no cutout block's tinted tile has a source tile to compare against");
    }

    [Fact]
    public void TintedTilesAreDistinctFromTheirDefaults()
    {
        var blocks = TintedBlocks.ToList();
        Assert.NotEmpty(blocks);
        foreach (var block in blocks)
        {
            var bt = block.BiomeTint!;
            for (var f = 0; f < 6; f++)
                if (bt.IsTinted(f))
                    Assert.NotEqual(block.FaceTiles[f], bt.Tinted[f]);
        }
    }
}
