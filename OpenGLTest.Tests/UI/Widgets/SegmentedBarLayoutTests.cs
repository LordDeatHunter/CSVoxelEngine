using System.Linq;
using VoxelEngine.UI.Widgets;
using Xunit;

namespace VoxelEngine.Tests.UI.Widgets;

/// <summary>The segmented bar's geometry: the pieces tile the bar exactly and the fill splits it into the two rows.</summary>
public sealed class SegmentedBarLayoutTests
{
    private const int Segment = 16;
    private const int Row = 16;

    [Theory]
    [InlineData(320f, 16f, 0.5f)]
    [InlineData(333f, 16f, 0.37f)]
    [InlineData(40f, 32f, 0.9f)]
    [InlineData(20f, 16f, 0.5f)]
    public void ThePiecesTileTheWholeBarWithoutGapsOrOverlap(float width, float height, float share)
    {
        var pieces = SegmentedBarLayout.Pieces(width, height, share, Segment, Row);

        var x = 0f;
        foreach (var piece in pieces)
        {
            Assert.InRange(piece.X, x - 1e-3f, x + 1e-3f);
            Assert.True(piece.Width > 0f);
            x += piece.Width;
        }
        Assert.InRange(x, width - 1e-3f, width + 1e-3f);
    }

    [Fact]
    public void ZeroIsAllEmptyAndOneIsAllFull()
    {
        Assert.All(SegmentedBarLayout.Pieces(320f, 16f, 0f, Segment, Row), p => Assert.False(p.Full));
        Assert.All(SegmentedBarLayout.Pieces(320f, 16f, 1f, Segment, Row), p => Assert.True(p.Full));
        // Out-of-range shares clamp
        Assert.All(SegmentedBarLayout.Pieces(320f, 16f, 3f, Segment, Row), p => Assert.True(p.Full));
        Assert.All(SegmentedBarLayout.Pieces(320f, 16f, -1f, Segment, Row), p => Assert.False(p.Full));
    }

    [Fact]
    public void TheFullPartsWidthFollowsTheShareToTheNearestTexel()
    {
        var pieces = SegmentedBarLayout.Pieces(320f, 16f, 0.5f, Segment, Row);
        var full = pieces.Where(p => p.Full).Sum(p => p.Width);
        Assert.InRange(full, 159f, 161f);
        // Full pieces come first, then empty ones
        Assert.True(pieces.SkipWhile(p => p.Full).All(p => !p.Full));
    }

    [Fact]
    public void ACutPieceSamplesAProportionalSliceOfItsSegment()
    {
        // The boundary lands inside a middle tile: both halves sample the same segment and together cover all of it
        var pieces = SegmentedBarLayout.Pieces(320f, 16f, 0.5f + 8f / 320f, Segment, Row);
        var cut = pieces.Where(p => p.SrcX0 >= Segment && p.SrcX1 <= Segment * 2).ToList();
        var halves = cut.Where(p => p.Width < 16f - 1e-3f).ToList();
        Assert.Equal(2, halves.Count);
        Assert.InRange(halves.Sum(p => p.Width), 15.99f, 16.01f);
        Assert.InRange(halves.Sum(p => p.SrcX1 - p.SrcX0), 15.99f, 16.01f);
    }

    [Fact]
    public void ABarNarrowerThanItsCapsStillDraws()
    {
        var pieces = SegmentedBarLayout.Pieces(10f, 16f, 1f, Segment, Row);
        Assert.NotEmpty(pieces);
        Assert.InRange(pieces.Sum(p => p.Width), 9.99f, 10.01f);
    }

    [Fact]
    public void AnEmptyAreaYieldsNothing()
    {
        Assert.Empty(SegmentedBarLayout.Pieces(0f, 16f, 0.5f, Segment, Row));
        Assert.Empty(SegmentedBarLayout.Pieces(100f, 0f, 0.5f, Segment, Row));
    }
}
