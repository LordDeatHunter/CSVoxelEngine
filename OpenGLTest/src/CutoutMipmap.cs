using System;

namespace silkgltriangle;

/// <summary>
/// Builds the mip chain of an alpha-tested (cutout) tile so its coverage survives minification.
/// A plain box filter averages a leaf's opaque texels with the holes between them, so once the
/// atlas samples its mips the averaged alpha drops below the cutoff and distant canopies thin out
/// to nothing. Each level here is filtered like a normal mip (colour alpha-weighted, so transparent
/// texels do not darken the edges), then its alpha is rescaled so the fraction of texels passing
/// the cutoff matches level 0. GL-free: <see cref="TextureAtlas"/> uploads the result.
/// </summary>
public static class CutoutMipmap
{
    private const int SearchIterations = 24;
    private const float MaxScale = 64f;

    /// <summary>Number of mip levels GL allocates for a <paramref name="w"/> x
    /// <paramref name="h"/> texture, level 0 included.</summary>
    public static int LevelCount(int w, int h)
    {
        int size = Math.Max(w, h);
        int levels = 1;
        while (size > 1)
        {
            size >>= 1;
            levels++;
        }
        return levels;
    }

    /// <summary>Fraction of the RGBA texels whose alpha passes <paramref name="cutoff"/>.</summary>
    public static float Coverage(ReadOnlySpan<byte> rgba, float cutoff)
    {
        int texels = rgba.Length / 4;
        if (texels == 0) return 0f;
        int passing = 0;
        for (int i = 0; i < texels; i++)
            if (rgba[i * 4 + 3] / 255f >= cutoff) passing++;
        return passing / (float)texels;
    }

    /// <summary>
    /// Levels 1..<see cref="LevelCount"/>-1 of an RGBA tile (top row first), each
    /// max(1, w&gt;&gt;l) x max(1, h&gt;&gt;l). Every level is filtered from the previous
    /// <em>unscaled</em> level, so the per-level alpha rescale never compounds.
    /// </summary>
    public static byte[][] BuildChain(byte[] level0, int w, int h, float cutoff)
    {
        int count = LevelCount(w, h);
        var chain = new byte[count - 1][];
        if (count == 1) return chain;

        // Working level, unscaled and unrounded: straight RGB plus alpha, 0..255.
        var src = new float[w * h * 4];
        for (int i = 0; i < src.Length; i++) src[i] = level0[i];
        bool anyNonZero = false, anyNonFull = false;
        for (int i = 3; i < level0.Length; i += 4)
        {
            anyNonZero |= level0[i] != 0;
            anyNonFull |= level0[i] != 255;
        }
        // An all-empty or all-opaque tile keeps its alpha exactly; nothing to preserve.
        bool rescale = anyNonZero && anyNonFull;
        float target = Coverage(level0, cutoff);

        int pw = w, ph = h;
        for (int l = 1; l < count; l++)
        {
            int dw = Math.Max(1, w >> l), dh = Math.Max(1, h >> l);
            var dst = new float[dw * dh * 4];
            for (int y = 0; y < dh; y++)
            {
                int y0 = Math.Min(2 * y, ph - 1), y1 = Math.Min(2 * y + 1, ph - 1);
                int ny = y1 == y0 ? 1 : 2;
                for (int x = 0; x < dw; x++)
                {
                    int x0 = Math.Min(2 * x, pw - 1), x1 = Math.Min(2 * x + 1, pw - 1);
                    int nx = x1 == x0 ? 1 : 2;
                    float r = 0, g = 0, b = 0, a = 0, pr = 0, pg = 0, pb = 0;
                    for (int sy = 0; sy < ny; sy++)
                    for (int sx = 0; sx < nx; sx++)
                    {
                        int s = (((sy == 0 ? y0 : y1) * pw) + (sx == 0 ? x0 : x1)) * 4;
                        float sa = src[s + 3];
                        r += src[s] * sa; g += src[s + 1] * sa; b += src[s + 2] * sa;
                        pr += src[s]; pg += src[s + 1]; pb += src[s + 2];
                        a += sa;
                    }
                    int n = nx * ny;
                    int d = (y * dw + x) * 4;
                    if (a > 0f)
                    {
                        dst[d] = r / a; dst[d + 1] = g / a; dst[d + 2] = b / a;
                    }
                    else
                    {
                        dst[d] = pr / n; dst[d + 1] = pg / n; dst[d + 2] = pb / n;
                    }
                    dst[d + 3] = a / n;
                }
            }

            float scale = rescale ? FindScale(dst, cutoff, target) : 1f;
            var level = new byte[dst.Length];
            for (int i = 0; i < dst.Length; i += 4)
            {
                level[i] = ToByte(dst[i]);
                level[i + 1] = ToByte(dst[i + 1]);
                level[i + 2] = ToByte(dst[i + 2]);
                level[i + 3] = ToByte(dst[i + 3] * scale);
            }
            chain[l - 1] = level;

            src = dst;
            pw = dw;
            ph = dh;
        }
        return chain;
    }

    /// <summary>Binary-searches the alpha scale whose coverage lands closest to
    /// <paramref name="target"/>; coverage of the scaled alpha only grows with the scale.</summary>
    private static float FindScale(float[] rgba, float cutoff, float target)
    {
        float lo = 0f, hi = MaxScale;
        for (int i = 0; i < SearchIterations; i++)
        {
            float mid = (lo + hi) * 0.5f;
            if (ScaledCoverage(rgba, mid, cutoff) < target) lo = mid;
            else hi = mid;
        }
        float cLo = ScaledCoverage(rgba, lo, cutoff);
        float cHi = ScaledCoverage(rgba, hi, cutoff);
        return MathF.Abs(cLo - target) < MathF.Abs(cHi - target) ? lo : hi;
    }

    private static float ScaledCoverage(float[] rgba, float scale, float cutoff)
    {
        int texels = rgba.Length / 4, passing = 0;
        for (int i = 3; i < rgba.Length; i += 4)
            if (ToByte(rgba[i] * scale) / 255f >= cutoff) passing++;
        return passing / (float)texels;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp(MathF.Round(v), 0f, 255f);
}
