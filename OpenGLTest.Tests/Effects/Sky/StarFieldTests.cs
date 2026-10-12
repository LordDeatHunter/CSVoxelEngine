using System;
using System.Numerics;
using VoxelEngine.Effects.Sky;
using Xunit;

namespace VoxelEngine.Tests.Effects.Sky;

/// <summary>
/// The GL-free twin of the night sky's star field (<see cref="StarField"/>, the <c>Stars</c> term in
/// sky-fragment.glsl): when the stars show, that every star sits whole inside the one cell the
/// shader evaluates for it, that the field is equally dense in every direction, and that star space
/// turns with the sun.
/// </summary>
public sealed class StarFieldTests
{
    [Fact]
    public void NoStarShowsWhileTheSunIsUp()
    {
        for (var e = 0f; e <= 1f; e += 0.01f)
            Assert.Equal(0f, StarField.Visibility(e));
    }

    [Fact]
    public void StarsAreFullyOutOnceTheSunIsWellDown()
    {
        Assert.Equal(1f, StarField.Visibility(-1f));
        Assert.Equal(1f, StarField.Visibility(WorldGenerationConfig.StarsFullBelowSunElevation));
    }

    [Fact]
    public void StarsFadeInSteadilyAsTheSunSinks()
    {
        // Dusk and dawn walk the same curve in opposite directions, so one monotone sweep covers both.
        var previous = StarField.Visibility(1f);
        for (var e = 1f; e >= -1f; e -= 0.002f)
        {
            var v = StarField.Visibility(e);
            Assert.InRange(v, 0f, 1f);
            Assert.True(v >= previous, $"visibility fell from {previous} to {v} as the sun sank to {e}");
            previous = v;
        }
    }

    [Fact]
    public void EveryStarSitsWholeInsideTheCellTheShaderLooksUp()
    {
        // The shader evaluates only the cell a pixel falls in, so a star must map back to its own
        // cell and keep CellMargin clear of every edge, or its soft point is clipped at a border.
        var n = WorldGenerationConfig.StarCellsPerFace;
        var stars = 0;
        for (var face = 0; face < 6; face++)
        for (var x = 0; x < n; x++)
        for (var y = 0; y < n; y++)
        {
            if (!StarField.TryGetStar(face, x, y, n, WorldGenerationConfig.StarDensity, out var dir, out var brightness))
                continue;
            stars++;
            Assert.Equal((face, x, y), StarField.CellOf(dir, n));
            Assert.InRange(brightness, 0f, 1f);

            var (_, uv) = StarField.FaceOf(dir);
            var g = (StarField.Warp(uv) * 0.5f + new Vector2(0.5f)) * n;
            var inCell = g - new Vector2(x, y);
            const float eps = 1e-3f;
            Assert.InRange(inCell.X, StarField.CellMargin - eps, 1f - StarField.CellMargin + eps);
            Assert.InRange(inCell.Y, StarField.CellMargin - eps, 1f - StarField.CellMargin + eps);
        }

        Assert.True(stars > 0, "the configured field holds no star at all");
    }

    [Fact]
    public void TheFieldIsEquallyDenseInEveryDirection()
    {
        // Expected stars per steradian, cell by cell, against each cell's exact solid angle: a cube
        // map without the per-cell weighting crowds stars toward the face corners.
        var n = WorldGenerationConfig.StarCellsPerFace;
        var min = float.MaxValue;
        var max = 0f;
        for (var x = 0; x < n; x++)
        for (var y = 0; y < n; y++)
        {
            var density = StarField.Probability(x, y, n, 1f) / CellSolidAngle(x, y, n);
            min = MathF.Min(min, density);
            max = MathF.Max(max, density);
        }

        Assert.True(max / min < 1.02f, $"star density varies {max / min:F3}x across a cube face");
    }

    [Fact]
    public void EveryDirectionFallsInExactlyOneCell()
    {
        var rng = new Random(77);
        var n = WorldGenerationConfig.StarCellsPerFace;
        for (var i = 0; i < 5000; i++)
        {
            var d = Vector3.Normalize(new Vector3(
                (float)rng.NextDouble() * 2f - 1f, (float)rng.NextDouble() * 2f - 1f, (float)rng.NextDouble() * 2f - 1f));
            var (face, x, y) = StarField.CellOf(d, n);
            Assert.InRange(face, 0, 5);
            Assert.InRange(x, 0, n - 1);
            Assert.InRange(y, 0, n - 1);
            // FaceDirection inverts FaceOf, so the cell lookup is a true partition of the sphere
            var (f, uv) = StarField.FaceOf(d);
            Assert.True(Vector3.Distance(d, Vector3.Normalize(StarField.FaceDirection(f, uv))) < 1e-5f);
        }
    }

    [Fact]
    public void ATwinkleOnlyEverDimsAStarByItsDepth()
    {
        var depth = WorldGenerationConfig.StarTwinkleDepth;
        foreach (var (rate, phase) in SomeTwinkles())
        for (var t = 0f; t < 120f; t += 0.0137f)
            Assert.InRange(StarField.Twinkle(t, rate, phase), 1f - depth - 1e-5f, 1f + 1e-5f);
    }

    [Fact]
    public void ATwinkleNeverJumpsBetweenFrames()
    {
        // The curve is smooth: at 240 fps no frame may step further than its steepest slope allows,
        // the sum of both sines' amplitudes times their rates at the fastest twinkle.
        const float dt = 1f / 240f;
        var steepest = WorldGenerationConfig.StarTwinkleDepth * MathF.Tau * WorldGenerationConfig.StarTwinkleRateMax
                       * (0.3f + 0.2f * 2.31f);
        foreach (var (rate, phase) in SomeTwinkles())
        for (var t = 0f; t < 60f; t += dt)
        {
            var step = MathF.Abs(StarField.Twinkle(t + dt, rate, phase) - StarField.Twinkle(t, rate, phase));
            Assert.True(step <= steepest * dt * 1.01f + 1e-5f, $"the twinkle stepped {step} in one frame at t = {t}");
        }
    }

    [Fact]
    public void StarsTwinkleOutOfStepAtTheirOwnRates()
    {
        var twinkles = SomeTwinkles();
        var rates = new System.Collections.Generic.HashSet<float>();
        var mean = 0f;
        foreach (var (rate, phase) in twinkles)
        {
            Assert.InRange(rate, WorldGenerationConfig.StarTwinkleRateMin, WorldGenerationConfig.StarTwinkleRateMax);
            Assert.InRange(phase, 0f, MathF.Tau);
            rates.Add(rate);
            mean += StarField.Twinkle(10f, rate, phase);
        }

        Assert.True(rates.Count > twinkles.Length / 2, "neighboring stars share their twinkle rate");
        // At one instant the stars sit all over their swing rather than dimming together
        mean /= twinkles.Length;
        var variance = 0f;
        foreach (var (rate, phase) in twinkles)
            variance += MathF.Pow(StarField.Twinkle(10f, rate, phase) - mean, 2f);
        var spread = MathF.Sqrt(variance / twinkles.Length);
        Assert.True(spread > 0.1f * WorldGenerationConfig.StarTwinkleDepth, $"the stars twinkle in step (spread {spread})");
    }

    // The twinkles of a run of neighboring cells on one face.
    private static (float Rate, float Phase)[] SomeTwinkles()
    {
        var twinkles = new (float, float)[64];
        for (var i = 0; i < twinkles.Length; i++)
            twinkles[i] = StarField.TwinkleOf(2, 10 + i % 8, 20 + i / 8);
        return twinkles;
    }

    // The exact solid angle of cell (x, y) on a face: the spherical quad through its four corner
    // directions, split into two triangles (Van Oosterom and Strackee), in double precision since
    // a cell is well under a thousandth of a steradian.
    private static float CellSolidAngle(int x, int y, int n)
    {
        var a = Corner(x, y, n);
        var b = Corner(x + 1, y, n);
        var c = Corner(x + 1, y + 1, n);
        var d = Corner(x, y + 1, n);
        return (float)(Triangle(a, b, c) + Triangle(a, c, d));
    }

    private static (double X, double Y, double Z) Corner(int x, int y, int n)
    {
        var p = StarField.FaceDirection(4, StarField.CellPointUv(x, y, n));
        var length = Math.Sqrt((double)p.X * p.X + (double)p.Y * p.Y + (double)p.Z * p.Z);
        return (p.X / length, p.Y / length, p.Z / length);
    }

    private static double Triangle((double X, double Y, double Z) a, (double X, double Y, double Z) b,
        (double X, double Y, double Z) c)
    {
        static double Dot((double X, double Y, double Z) u, (double X, double Y, double Z) v) =>
            u.X * v.X + u.Y * v.Y + u.Z * v.Z;
        var cross = (b.Y * c.Z - b.Z * c.Y, b.Z * c.X - b.X * c.Z, b.X * c.Y - b.Y * c.X);
        var numerator = Math.Abs(Dot(a, cross));
        var denominator = 1.0 + Dot(a, b) + Dot(b, c) + Dot(c, a);
        return 2.0 * Math.Atan2(numerator, denominator);
    }
}
