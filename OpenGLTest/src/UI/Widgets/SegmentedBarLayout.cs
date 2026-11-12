using System;
using System.Collections.Generic;

namespace VoxelEngine.UI.Widgets;

/// <summary>One horizontal slice of a segmented bar: where it lands (<see cref="X"/>, <see cref="Width"/>, from the
/// bar's left edge, in canvas units), which columns of one 3-segment strip it samples (<see cref="SrcX0"/> to
/// <see cref="SrcX1"/>, in texture pixels) and which row of the sheet it belongs to.</summary>
public readonly record struct BarPiece(float X, float Width, float SrcX0, float SrcX1, bool Full);

/// <summary>
/// GL-free geometry of a progress bar drawn from a 3-segment strip: a left cap, a middle that repeats and a right
/// cap, in two rows (full and empty). The strip is scaled so its row height equals the bar's height; the middle is
/// tiled with the last tile cropped, so any width works. The fill share splits the bar at one x, snapped to whole
/// texture pixels so the edge never lands mid-texel, and every piece on one side of it samples the full row, the
/// rest the empty row. Reuse it for any bar with that strip shape (see <c>SegmentedBarRenderer</c>).
/// </summary>
public static class SegmentedBarLayout
{
    /// <summary>The pieces that tile a bar of <paramref name="width"/> by <paramref name="height"/>, left to right:
    /// each segment of the strip, cut at the fill boundary into a full-row part and an empty-row part. A bar
    /// narrower than its two caps still draws, with the caps squeezed to fit.</summary>
    /// <param name="width">Bar width in canvas units.</param>
    /// <param name="height">Bar height in canvas units (one row of the sheet is scaled to it).</param>
    /// <param name="share">Filled share, clamped to 0..1.</param>
    /// <param name="segmentPx">Width of one strip segment in texture pixels (the caps and the repeating middle).</param>
    /// <param name="rowPx">Height of one sheet row in texture pixels.</param>
    public static List<BarPiece> Pieces(float width, float height, float share, int segmentPx, int rowPx)
    {
        var pieces = new List<BarPiece>();
        if (width <= 0f || height <= 0f || segmentPx <= 0 || rowPx <= 0)
            return pieces;

        var scale = height / rowPx;
        var tile = segmentPx * scale;
        var cap = MathF.Min(tile, width * 0.5f);
        var capSrcShare = cap / tile;

        // The boundary between full and empty, on a whole destination texel
        var fillX = MathF.Round(Math.Clamp(share, 0f, 1f) * width / scale) * scale;
        fillX = Math.Clamp(fillX, 0f, width);

        // Left cap, the middle tiles, right cap: each as (x, width, source columns)
        AddSegment(pieces, 0f, cap, 0f, segmentPx * capSrcShare, fillX);
        var middleStart = cap;
        var middleEnd = width - cap;
        for (var x = middleStart; x < middleEnd - 1e-4f; x += tile)
        {
            var w = MathF.Min(tile, middleEnd - x);
            AddSegment(pieces, x, w, segmentPx, segmentPx + segmentPx * (w / tile), fillX);
        }
        AddSegment(pieces, width - cap, cap, segmentPx * 3 - segmentPx * capSrcShare, segmentPx * 3, fillX);
        return pieces;
    }

    // Adds one segment, split at fillX into the full part (left of it) and the empty part (right of it).
    private static void AddSegment(List<BarPiece> pieces, float x, float w, float src0, float src1, float fillX)
    {
        if (w <= 0f)
            return;
        var right = x + w;
        float SrcAt(float px) => src0 + (px - x) / w * (src1 - src0);

        if (fillX >= right)
        {
            pieces.Add(new BarPiece(x, w, src0, src1, true));
            return;
        }
        if (fillX <= x)
        {
            pieces.Add(new BarPiece(x, w, src0, src1, false));
            return;
        }
        var srcMid = SrcAt(fillX);
        pieces.Add(new BarPiece(x, fillX - x, src0, srcMid, true));
        pieces.Add(new BarPiece(fillX, right - fillX, srcMid, src1, false));
    }
}
