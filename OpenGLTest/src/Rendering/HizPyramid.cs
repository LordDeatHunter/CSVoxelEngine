using System;

namespace VoxelEngine.Rendering;

/// <summary>
/// A max-depth pyramid over a frame's window depth: the C# reference of
/// <c>hiz-build-compute.glsl</c> (the build) and of <c>hiz-cull-compute.glsl</c>'s lookup
/// (<see cref="LevelFor"/>, <see cref="MaxOver"/>); keep them in step (see the shader-sync skill).
/// <para>
/// Level 0 is half the frame, padded per axis to a power of two (<see cref="PaddedSize"/>), and
/// level <c>L</c> is <c>max(1, pad &gt;&gt; L)</c> per axis down to 1 x 1, the mip chain
/// <c>glTexStorage2D</c> allocates for that size. Every texel is the max of its 2x2 source texels
/// (level 0: of the frame's pixels), each tap clamped to its source's last row and column, so a
/// texel holds the max over at least every source pixel it covers (a padded texel repeats an edge
/// pixel, which only raises the max). Pixel <c>x</c> therefore falls in level <c>L</c>'s texel
/// <c>x &gt;&gt; (L + 1)</c>, whose value is at least its depth.
/// </para>
/// Render thread only; the storage grows only, so a rebuild at the same or a smaller size allocates
/// nothing.
/// </summary>
internal sealed class HizPyramid
{
    private const int MaxLevels = 32;

    private float[] _texels = [];
    private readonly int[] _offset = new int[MaxLevels];
    private readonly int[] _width = new int[MaxLevels];
    private readonly int[] _height = new int[MaxLevels];

    /// <summary>How many levels the last <see cref="Build"/> made (0 before the first).</summary>
    internal int Levels { get; private set; }

    /// <summary>The level-0 size along an axis of <paramref name="pixels"/> pixels: the smallest power
    /// of two at or above half of them, rounded up.</summary>
    internal static int PaddedSize(int pixels)
    {
        var half = (pixels + 1) / 2;
        var size = 1;
        while (size < half) size <<= 1;
        return size;
    }

    /// <summary>The level count of a padded level 0 of <paramref name="paddedWidth"/> x
    /// <paramref name="paddedHeight"/>: down to 1 x 1.</summary>
    internal static int LevelCount(int paddedWidth, int paddedHeight)
    {
        var levels = 1;
        for (var size = Math.Max(paddedWidth, paddedHeight); size > 1; size >>= 1) levels++;
        return levels;
    }

    /// <summary>
    /// Builds the pyramid over a <paramref name="width"/> x <paramref name="height"/> window depth
    /// image, row-major with row 0 at the bottom.
    /// </summary>
    internal void Build(ReadOnlySpan<float> depth, int width, int height)
    {
        var padW = PaddedSize(width);
        var padH = PaddedSize(height);
        Levels = LevelCount(padW, padH);

        var total = 0;
        for (var level = 0; level < Levels; level++)
        {
            _offset[level] = total;
            _width[level] = Math.Max(1, padW >> level);
            _height[level] = Math.Max(1, padH >> level);
            total += _width[level] * _height[level];
        }

        if (_texels.Length < total) _texels = new float[total];

        Reduce(depth, width, height, _texels.AsSpan(0, _width[0] * _height[0]), _width[0], _height[0]);
        for (var level = 1; level < Levels; level++)
        {
            ReadOnlySpan<float> source = _texels.AsSpan(_offset[level - 1], _width[level - 1] * _height[level - 1]);
            Reduce(source, _width[level - 1], _height[level - 1],
                _texels.AsSpan(_offset[level], _width[level] * _height[level]), _width[level], _height[level]);
        }
    }

    private static void Reduce(ReadOnlySpan<float> source, int sourceWidth, int sourceHeight,
        Span<float> target, int targetWidth, int targetHeight)
    {
        for (var y = 0; y < targetHeight; y++)
        {
            var row0 = Math.Min(2 * y, sourceHeight - 1) * sourceWidth;
            var row1 = Math.Min(2 * y + 1, sourceHeight - 1) * sourceWidth;
            for (var x = 0; x < targetWidth; x++)
            {
                var a = Math.Min(2 * x, sourceWidth - 1);
                var b = Math.Min(2 * x + 1, sourceWidth - 1);
                target[y * targetWidth + x] = MathF.Max(
                    MathF.Max(source[row0 + a], source[row0 + b]),
                    MathF.Max(source[row1 + a], source[row1 + b]));
            }
        }
    }

    /// <summary>
    /// The finest level at which <paramref name="rect"/> spans at most two texels along each axis,
    /// capped at the top level.
    /// </summary>
    internal static int LevelFor(in HizRect rect, int levels)
    {
        var level = 0;
        while (level < levels - 1
               && ((rect.X1 >> (level + 1)) - (rect.X0 >> (level + 1)) > 1
                   || (rect.Y1 >> (level + 1)) - (rect.Y0 >> (level + 1)) > 1))
            level++;
        return level;
    }

    /// <summary>
    /// A depth at least the max over every pixel of <paramref name="rect"/> (which must lie inside the
    /// built frame): the max of the up to 2x2 texels covering it at <see cref="LevelFor"/>.
    /// </summary>
    internal float MaxOver(in HizRect rect)
    {
        var level = LevelFor(rect, Levels);
        var w = _width[level];
        var h = _height[level];
        var offset = _offset[level];
        var tx0 = Math.Min(rect.X0 >> (level + 1), w - 1);
        var tx1 = Math.Min(rect.X1 >> (level + 1), w - 1);
        var ty0 = Math.Min(rect.Y0 >> (level + 1), h - 1);
        var ty1 = Math.Min(rect.Y1 >> (level + 1), h - 1);
        return MathF.Max(
            MathF.Max(_texels[offset + ty0 * w + tx0], _texels[offset + ty0 * w + tx1]),
            MathF.Max(_texels[offset + ty1 * w + tx0], _texels[offset + ty1 * w + tx1]));
    }
}
