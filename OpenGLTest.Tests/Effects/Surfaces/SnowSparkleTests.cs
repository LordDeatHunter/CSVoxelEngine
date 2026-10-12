using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using VoxelEngine.Graphics;
using Xunit;

namespace VoxelEngine.Tests.Effects.Surfaces;

/// <summary>
/// The snow glints in test-fragment.glsl light whole texels: each sits on a world-space grid of
/// <c>SnowTexels</c> cells per block, meant to be the snow tile's own pixel grid (one tile spans
/// one block face, <c>Chunk.TiledUv</c>). If the art changes size and the constant does not,
/// a glint straddles two texels of the art instead of flashing one, which nothing else catches.
/// </summary>
public sealed class SnowSparkleTests
{
    public SnowSparkleTests() => TestContent.EnsureLoaded();

    [Fact]
    public void TheGlintGridIsTheSnowTilesPixelGrid()
    {
        var source = File.ReadAllText(Path.Combine(TestContent.FindAssetsRoot(), "shaders", "test-fragment.glsl"));
        var match = Regex.Match(source, @"const float SnowTexels = ([0-9.]+);");
        Assert.True(match.Success, "test-fragment.glsl declares no SnowTexels constant");
        var texels = float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);

        // Every atlas tile shares one size (TextureAtlas), the snow tile among them
        Assert.Equal((float)TextureAtlas.TileWidth, texels);
        Assert.Equal((float)TextureAtlas.TileHeight, texels);
    }
}
