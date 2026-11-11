using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Items;
using VoxelEngine.UI;
using Xunit;
using static VoxelEngine.Config.RarityStyleConfig;

namespace VoxelEngine.Tests.UI;

/// <summary>
/// The rarity chrome (the twin of <c>ui-fragment.glsl</c>'s <c>uChrome</c> mode): bounded by its opacity, brighter
/// on the rim than in the middle, a sheen that only ever enters and leaves off the box, a pulse within its depth,
/// and sparkles that twinkle. Relations only: the GPU's <c>sin</c> is not bit-exact with the twin's.
/// </summary>
public sealed class RarityChromeTests
{
    private const int Grid = 17;

    // A list row and a tooltip box, with the borders their renderer passes.
    private static readonly (Vector2 Size, float Border, bool WashFromTop)[] Boxes =
    [
        (new Vector2(400f, 38f), UiTheme.Border.Thin, false),
        (new Vector2(300f, 160f), UiTheme.Border.Standard, true),
    ];

    private static readonly Rarity[] ChromeRarities =
        Enum.GetValues<Rarity>().Where(r => RarityStyle.Of(r).HasChrome).ToArray();

    private static readonly Rarity Top = Enum.GetValues<Rarity>()[^1];

    private const float SweepSeconds = SheenPeriodSeconds * SheenSweepShare;

    // Halfway through the rest between two sweeps.
    private const float RestingTime = SweepSeconds + (SheenPeriodSeconds - SweepSeconds) * 0.5f;

    private static IEnumerable<Vector2> UvGrid()
    {
        for (var i = 0; i < Grid; i++)
        for (var j = 0; j < Grid; j++)
            yield return new Vector2((float)i / (Grid - 1), (float)j / (Grid - 1));
    }

    [Fact]
    public void TheChromeNeverExceedsItsOpacity()
    {
        float[] times = [0f, SweepSeconds * 0.25f, SweepSeconds * 0.5f, SweepSeconds * 0.8f, RestingTime,
            PulsePeriodSeconds * 0.75f];
        foreach (var rarity in ChromeRarities)
        foreach (var (size, border, washFromTop) in Boxes)
        foreach (var time in times)
        {
            var inputs = RarityChrome.For(RarityStyle.Of(rarity), size, time, border, washFromTop);
            foreach (var uv in UvGrid())
            {
                var shade = RarityChrome.Shade(uv, inputs);
                Assert.InRange(shade.W, 0f, inputs.Color.W + 1e-6f);
                Assert.InRange(shade.X, 0f, 1f + 1e-5f);
                Assert.InRange(shade.Y, 0f, 1f + 1e-5f);
                Assert.InRange(shade.Z, 0f, 1f + 1e-5f);
            }
        }
    }

    [Fact]
    public void TheRimOutshinesTheMiddle()
    {
        foreach (var rarity in ChromeRarities)
        foreach (var (size, border, washFromTop) in Boxes)
        {
            var inputs = RarityChrome.For(RarityStyle.Of(rarity), size, RestingTime, border, washFromTop);
            // A sparkle is a point light that may land anywhere (ASparkleTwinkles covers it), so the rim is
            // weighed against the middle with the sparkles off.
            inputs = inputs with { Layers = inputs.Layers with { W = 0f } };
            var middle = RarityChrome.Shade(new Vector2(0.5f, 0.5f), inputs).W;
            Assert.True(RarityChrome.Shade(new Vector2(0.5f, 0f), inputs).W > middle, $"{rarity}'s top edge");
            Assert.True(RarityChrome.Shade(new Vector2(1f, 0.5f), inputs).W > middle, $"{rarity}'s right edge");
        }
    }

    [Fact]
    public void TheSheenEntersAndLeavesOffTheBoxAndRestsBetweenSweeps()
    {
        var style = RarityStyle.Of(Top);
        foreach (var (size, border, washFromTop) in Boxes)
        {
            Vector2[] probes = [Vector2.Zero, new(size.X, 0f), new(0f, size.Y), size, size * 0.5f];

            // The band starts and ends off the box: nothing of it shows at a corner or the center.
            foreach (var time in new[] { 0f, SweepSeconds - 1e-4f })
            {
                var edge = RarityChrome.For(style, size, time, border, washFromTop);
                foreach (var p in probes)
                    Assert.InRange(RarityChrome.Sheen(p, edge), 0f, 1e-4f);
            }

            // Mid-sweep it lights part of the box.
            var mid = RarityChrome.For(style, size, SweepSeconds * 0.5f, border, washFromTop);
            Assert.True(UvGrid().Any(uv => RarityChrome.Sheen(uv * size, mid) > 0.5f), "the sheen crosses the box");

            // Its center only moves forward through the sweep.
            var previous = float.NegativeInfinity;
            for (var i = 0; i < 32; i++)
            {
                var center = RarityChrome.For(style, size, SweepSeconds * i / 32f, border, washFromTop).Motion.X;
                Assert.True(center >= previous, $"the sheen moved back at step {i}");
                previous = center;
            }
            Assert.True(previous > RarityChrome.For(style, size, 0f, border, washFromTop).Motion.X);

            // Between sweeps the layer is off.
            Assert.Equal(0f, RarityChrome.For(style, size, RestingTime, border, washFromTop).Layers.Z);
        }
    }

    [Fact]
    public void ThePulseBreathesWithinItsDepth()
    {
        foreach (var depth in new[] { 0.25f, 0.5f, 1f })
        {
            var low = float.PositiveInfinity;
            var high = float.NegativeInfinity;
            for (var i = 0; i <= 200; i++)
            {
                var pulse = RarityChrome.Pulse(PulsePeriodSeconds * i / 200f, depth);
                Assert.InRange(pulse, 1f - depth - 1e-6f, 1f + 1e-6f);
                low = MathF.Min(low, pulse);
                high = MathF.Max(high, pulse);
            }
            Assert.True(low < 1f - depth + 0.01f, $"a depth of {depth} breathes all the way down");
            Assert.True(high > 1f - 0.01f, $"a depth of {depth} breathes all the way up");
        }

        for (var i = 0; i <= 50; i++)
            Assert.Equal(1f, RarityChrome.Pulse(PulsePeriodSeconds * i / 50f, 0f));
    }

    [Fact]
    public void ASparkleTwinkles()
    {
        var size = Boxes[1].Size;
        var inputs = RarityChrome.For(RarityStyle.Of(Top), size, 0f, Boxes[1].Border, true);
        var everyCell = inputs with { Box = inputs.Box with { W = 1f } };

        // The star of one cell sits at its hashed spot.
        var cell = new Vector2(3f, 2f);
        var h = RarityChrome.Hash(cell);
        var spot = (cell + new Vector2(0.25f) + 0.5f * new Vector2(Fract(h * 17f), Fract(h * 31f))) * inputs.Shape.W;

        var brightest = 0f;
        var darkest = 1f;
        for (var i = 0; i < 100; i++)
        {
            var phased = everyCell with { Motion = everyCell.Motion with { Z = i / 100f } };
            var sparkle = RarityChrome.Sparkle(spot, phased);
            brightest = MathF.Max(brightest, sparkle);
            darkest = MathF.Min(darkest, sparkle);
        }
        Assert.True(brightest > 0.5f, "the star lights up at some phase");
        Assert.True(darkest < 0.05f, "the star goes dark at another");

        // At the configured density the stars twinkle out of step: at every phase some are lit and some are dark.
        var field = new Vector2(600f, 400f);
        var sparse = RarityChrome.For(RarityStyle.Of(Top), field, 0f, Boxes[1].Border, true);
        var stars = new List<Vector2>();
        for (var x = 0f; x < field.X / sparse.Shape.W - 1f; x++)
        for (var y = 0f; y < field.Y / sparse.Shape.W - 1f; y++)
        {
            var starCell = new Vector2(x, y);
            var hash = RarityChrome.Hash(starCell);
            if (hash >= sparse.Box.W) continue;
            var u = hash / sparse.Box.W;
            stars.Add((starCell + new Vector2(0.25f) + 0.5f * new Vector2(Fract(u * 17f), Fract(u * 31f))) * sparse.Shape.W);
        }
        Assert.NotEmpty(stars);
        for (var i = 0; i < 20; i++)
        {
            var phased = sparse with { Motion = sparse.Motion with { Z = i / 20f } };
            var lit = stars.Count(s => RarityChrome.Sparkle(s, phased) > 0.5f);
            Assert.InRange(lit, 1, stars.Count - 1);
        }

        // With no cell holding a star nothing ever lights.
        var none = inputs with { Box = inputs.Box with { W = 0f } };
        for (var i = 0; i < 10; i++)
        {
            var phased = none with { Motion = none.Motion with { Z = i / 10f } };
            Assert.All(UvGrid(), uv => Assert.Equal(0f, RarityChrome.Sparkle(uv * size, phased)));
        }
    }

    private static float Fract(float x) => x - MathF.Floor(x);
}
