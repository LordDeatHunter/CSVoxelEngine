using System;
using VoxelEngine.Juice;
using Xunit;

namespace VoxelEngine.Tests.Juice;

/// <summary>
/// The placement pop's curve (<see cref="PlacePop"/>): the block starts a little too big, settles
/// to exactly its cell size and never grows past the start.
/// </summary>
public sealed class PlacePopTests
{
    [Fact]
    public void ThePopStartsGrownAndSettlesToExactlyZero()
    {
        const float seconds = 0.22f, grow = 0.1f;
        Assert.Equal(grow, PlacePop.Grow(0f, seconds, grow), 4);
        Assert.Equal(0f, PlacePop.Grow(seconds, seconds, grow));
        Assert.Equal(0f, PlacePop.Grow(seconds * 2f, seconds, grow));

        var peak = 0f;
        var dip = 0f;
        for (var t = 0f; t <= seconds * 1.5f; t += seconds / 60f)
        {
            var value = PlacePop.Grow(t, seconds, grow);
            peak = MathF.Max(peak, value);
            dip = MathF.Min(dip, value);
        }

        Assert.True(peak <= grow + 1e-5f, $"the pop grew past its start: {peak}");
        Assert.True(dip < 0f, "the pop never overshoots below its cell size");
        Assert.True(dip > -0.25f * grow, $"the overshoot is too deep: {dip}");
    }

    [Fact]
    public void AZeroLengthPopNeverMoves()
    {
        Assert.Equal(0f, PlacePop.Grow(0f, 0f, 0.1f));
        Assert.Equal(0f, PlacePop.Grow(0.05f, -1f, 0.1f));
    }
}
