using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// <c>CutoutMipmap</c> is the GL-free half of the atlas's cutout mips: the level dimensions,
/// the alpha-weighted colour filter and the per-level alpha rescale that keeps an alpha-tested
/// tile's coverage at level 0's. The upload itself needs a live GL context.
/// </summary>
public sealed class CutoutMipmapTests
{
    private const float Cutoff = WorldGenerationConfig.AlphaCutoff;

    private static byte[] Tile(int w, int h, Func<int, int, (byte r, byte g, byte b, byte a)> texel)
    {
        var data = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            var (r, g, b, a) = texel(x, y);
            int i = (y * w + x) * 4;
            data[i] = r; data[i + 1] = g; data[i + 2] = b; data[i + 3] = a;
        }
        return data;
    }

    private static byte[] RandomAlphaTile(int w, int h, int seed)
    {
        var rng = new Random(seed);
        return Tile(w, h, (_, _) => (40, 160, 40, (byte)rng.Next(256)));
    }

    [Theory]
    [InlineData(16, 16)]
    [InlineData(32, 8)]
    [InlineData(5, 12)]
    [InlineData(1, 7)]
    public void ChainHalvesDownToOnePixel(int w, int h)
    {
        var chain = CutoutMipmap.BuildChain(RandomAlphaTile(w, h, 1), w, h, Cutoff);

        Assert.Equal(CutoutMipmap.LevelCount(w, h) - 1, chain.Length);
        int pw = w, ph = h;
        foreach (var level in chain)
        {
            pw = Math.Max(1, pw / 2);
            ph = Math.Max(1, ph / 2);
            Assert.Equal(pw * ph * 4, level.Length);
        }
        Assert.Equal(4, chain[^1].Length);
    }

    [Fact]
    public void OpaqueAndEmptyTilesKeepTheirAlpha()
    {
        foreach (byte alpha in new byte[] { 0, 255 })
        {
            var rng = new Random(alpha);
            var tile = Tile(16, 16, (_, _) => ((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), alpha));
            foreach (var level in CutoutMipmap.BuildChain(tile, 16, 16, Cutoff))
                for (int i = 3; i < level.Length; i += 4)
                    Assert.Equal(alpha, level[i]);
        }
    }

    [Fact]
    public void RescaledCoverageTracksLevelZeroBetterThanBoxFilter()
    {
        const int size = 32;
        var tile = RandomAlphaTile(size, size, 12345);
        float target = CutoutMipmap.Coverage(tile, Cutoff);
        var chain = CutoutMipmap.BuildChain(tile, size, size, Cutoff);

        // Plain box filter of the alpha, level by level, for comparison.
        var box = new double[size * size];
        for (int i = 0; i < box.Length; i++) box[i] = tile[i * 4 + 3];
        int dim = size;
        bool anyStrictlyBetter = false;
        foreach (var level in chain)
        {
            int next = Math.Max(1, dim / 2);
            var reduced = new double[next * next];
            for (int y = 0; y < next; y++)
            for (int x = 0; x < next; x++)
                reduced[y * next + x] = (box[2 * y * dim + 2 * x] + box[2 * y * dim + 2 * x + 1]
                    + box[(2 * y + 1) * dim + 2 * x] + box[(2 * y + 1) * dim + 2 * x + 1]) / 4.0;
            box = reduced;
            dim = next;

            float boxCoverage = box.Count(a => Math.Round(a) / 255.0 >= Cutoff) / (float)box.Length;
            float rescaled = CutoutMipmap.Coverage(level, Cutoff);
            float boxError = MathF.Abs(boxCoverage - target);
            float rescaledError = MathF.Abs(rescaled - target);
            Assert.True(rescaledError <= boxError + 1e-6f,
                $"{dim}x{dim}: rescaled coverage {rescaled} vs box {boxCoverage}, target {target}");
            if (rescaledError < boxError - 1e-6f) anyStrictlyBetter = true;
        }
        Assert.True(anyStrictlyBetter);
    }

    [Fact]
    public void ColourIsAlphaWeightedSoTransparentTexelsDoNotDarken()
    {
        // Opaque green checkered against transparent black.
        var tile = Tile(16, 16, (x, y) => ((x + y) % 2 == 0 ? ((byte)0, (byte)255, (byte)0, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0)));
        foreach (var level in CutoutMipmap.BuildChain(tile, 16, 16, Cutoff))
            for (int i = 0; i < level.Length; i += 4)
            {
                Assert.True(level[i + 1] > level[i] && level[i + 1] > level[i + 2]);
                Assert.True(level[i + 1] >= 250, $"green darkened to {level[i + 1]}");
            }
    }

    [Fact]
    public void ChainIsDeterministic()
    {
        var tile = RandomAlphaTile(16, 16, 7);
        var a = CutoutMipmap.BuildChain(tile, 16, 16, Cutoff);
        var b = CutoutMipmap.BuildChain((byte[])tile.Clone(), 16, 16, Cutoff);
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++) Assert.Equal(a[i], b[i]);
    }

    [Fact]
    public void CutoutTilesAreExactlyTheTilesOfCutoutBlocks()
    {
        TestContent.EnsureLoaded();
        var tiles = BlockType.CutoutTiles();
        Assert.NotEmpty(tiles);

        var cutouts = BlockType.ById.Where(t => t is { Cutout: true }).Select(t => t!).ToList();
        IEnumerable<ushort> TilesOf(BlockType t) =>
            (t.BakedModel?.Quads.Select(q => q.Tile) ?? []).Concat(t.FaceTiles);

        foreach (var block in cutouts)
            foreach (var tile in TilesOf(block))
                Assert.Contains(tile, tiles);
        foreach (var tile in tiles)
            Assert.Contains(cutouts, b => TilesOf(b).Contains(tile));
        Assert.Equal(tiles.Distinct().Count(), tiles.Count);
    }
}
