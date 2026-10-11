using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// GL-free twin of the night sky's star field, drawn by <c>Stars</c> in sky-fragment.glsl; keep
/// the two in step. Stars live on a grid over the sphere of directions, not on the screen, so they
/// stay put while the camera turns: each direction picks one face of a cube, an equi-angular warp
/// (<c>atan</c> of the face coordinates) turns that face into a near-uniform-angle square, and the
/// square is cut into <see cref="WorldGenerationConfig.StarCellsPerFace"/>² cells. A cell holds at
/// most one star, kept <see cref="CellMargin"/> clear of its edges so the shader, which only looks
/// at the one cell a pixel falls in, never clips a star's soft point at a cell border. The warp
/// leaves a cell at a face edge a little smaller in solid angle than one at the face centre, so
/// <see cref="Probability"/> scales each cell's chance by its relative solid angle and the field
/// is equally dense in every direction (no cube-shaped clumping at the face corners). The stars
/// stay put in the sky; only <see cref="Visibility"/> moves.
/// </summary>
public static class StarField
{
    /// <summary>A quarter turn, the angle each half of a cube face spans from its centre.</summary>
    private const float QuarterPi = MathF.PI / 4f;

    /// <summary>
    /// How far a star stays from its cell's edges, as a fraction of the cell, so its soft point
    /// (a few pixels wide) never reaches into a neighbouring cell the shader would not evaluate.
    /// </summary>
    public const float CellMargin = 0.25f;

    /// <summary>
    /// How visible the stars are for a sun at sine-elevation <paramref name="sunElevation"/>: 0 by
    /// day, rising smoothly over dusk from <see cref="WorldGenerationConfig.StarsGoneAboveSunElevation"/>
    /// to 1 at <see cref="WorldGenerationConfig.StarsFullBelowSunElevation"/>, and the same in reverse
    /// at dawn (the sun climbing back through the same band).
    /// </summary>
    public static float Visibility(float sunElevation) =>
        1f - MathUtils.SmoothStep(WorldGenerationConfig.StarsFullBelowSunElevation,
            WorldGenerationConfig.StarsGoneAboveSunElevation, sunElevation);

    /// <summary>
    /// The cube face (0..5: +X, -X, +Y, -Y, +Z, -Z) a direction falls on and its face coordinates,
    /// each in -1..1 (the direction divided by its largest component).
    /// </summary>
    public static (int Face, Vector2 Uv) FaceOf(Vector3 d)
    {
        var ax = MathF.Abs(d.X);
        var ay = MathF.Abs(d.Y);
        var az = MathF.Abs(d.Z);
        if (ax >= ay && ax >= az) return (d.X > 0f ? 0 : 1, new Vector2(d.Z, d.Y) / ax);
        if (ay >= az) return (d.Y > 0f ? 2 : 3, new Vector2(d.X, d.Z) / ay);
        return (d.Z > 0f ? 4 : 5, new Vector2(d.X, d.Y) / az);
    }

    /// <summary>The (unnormalised) direction through face coordinates <paramref name="uv"/> of face <paramref name="face"/>; the inverse of <see cref="FaceOf"/>.</summary>
    public static Vector3 FaceDirection(int face, Vector2 uv) => face switch
    {
        0 => new Vector3(1f, uv.Y, uv.X),
        1 => new Vector3(-1f, uv.Y, uv.X),
        2 => new Vector3(uv.X, 1f, uv.Y),
        3 => new Vector3(uv.X, -1f, uv.Y),
        4 => new Vector3(uv.X, uv.Y, 1f),
        _ => new Vector3(uv.X, uv.Y, -1f),
    };

    /// <summary>The equi-angular warp: face coordinates to a -1..1 square that is near-uniform in angle.</summary>
    public static Vector2 Warp(Vector2 uv) => new(MathF.Atan(uv.X) / QuarterPi, MathF.Atan(uv.Y) / QuarterPi);

    /// <summary>The inverse of <see cref="Warp"/>.</summary>
    public static Vector2 Unwarp(Vector2 s) => new(MathF.Tan(s.X * QuarterPi), MathF.Tan(s.Y * QuarterPi));

    /// <summary>The cell a star-space direction falls in: its cube face and the cell's column and row.</summary>
    public static (int Face, int X, int Y) CellOf(Vector3 d, int cellsPerFace)
    {
        var (face, uv) = FaceOf(d);
        var g = (Warp(uv) * 0.5f + new Vector2(0.5f)) * cellsPerFace;
        return (face, Math.Min((int)MathF.Floor(g.X), cellsPerFace - 1),
            Math.Min((int)MathF.Floor(g.Y), cellsPerFace - 1));
    }

    /// <summary>
    /// The face coordinates of a point given in cell units (<paramref name="cellX"/> + fraction):
    /// the warped square position mapped back through <see cref="Unwarp"/>.
    /// </summary>
    public static Vector2 CellPointUv(float cellX, float cellY, int cellsPerFace) =>
        Unwarp(new Vector2(cellX, cellY) / cellsPerFace * 2f - Vector2.One);

    /// <summary>
    /// The solid angle of a small patch of warped square at face coordinates <paramref name="uv"/>,
    /// relative to the same patch at the face centre: the cube's own (1 + u² + v²)^-3/2 density
    /// times the warp's stretch (1 + u²)(1 + v²). Between about 0.7 and 1.
    /// </summary>
    public static float RelativeSolidAngle(Vector2 uv)
    {
        var u2 = uv.X * uv.X;
        var v2 = uv.Y * uv.Y;
        return (1f + u2) * (1f + v2) / MathF.Pow(1f + u2 + v2, 1.5f);
    }

    /// <summary>
    /// The chance cell (<paramref name="face"/>, <paramref name="x"/>, <paramref name="y"/>) holds
    /// a star: <paramref name="density"/> scaled by the cell's relative solid angle at its centre.
    /// </summary>
    public static float Probability(int x, int y, int cellsPerFace, float density) =>
        density * RelativeSolidAngle(CellPointUv(x + 0.5f, y + 0.5f, cellsPerFace));

    /// <summary>Integer hash (lowbias32); twin of <c>StarHash</c> in sky-fragment.glsl.</summary>
    public static uint Hash(uint x)
    {
        unchecked
        {
            x ^= x >> 16;
            x *= 0x7feb352du;
            x ^= x >> 15;
            x *= 0x846ca68bu;
            x ^= x >> 16;
            return x;
        }
    }

    /// <summary>A cell's hash, the seed of everything about its star; twin of <c>StarCellHash</c>.</summary>
    public static uint CellHash(int face, int x, int y) =>
        unchecked(Hash((uint)x ^ Hash((uint)y ^ Hash((uint)face + 0x9e3779b9u))));

    /// <summary>The top 24 bits of a hash as a uniform value in [0, 1).</summary>
    public static float Unit(uint h) => (h >> 8) * (1f / 16777216f);

    /// <summary>
    /// The star in cell (<paramref name="face"/>, <paramref name="x"/>, <paramref name="y"/>), if
    /// the cell holds one: its unit star-space direction and its brightness fraction (0..1, mostly
    /// faint: a power law with exponent <see cref="WorldGenerationConfig.StarBrightnessCurve"/>
    /// over a floor of <see cref="WorldGenerationConfig.StarFaintest"/>).
    /// </summary>
    public static bool TryGetStar(int face, int x, int y, int cellsPerFace, float density,
        out Vector3 direction, out float brightness)
    {
        var h = CellHash(face, x, y);
        direction = default;
        brightness = 0f;
        if (Unit(h) >= Probability(x, y, cellsPerFace, density)) return false;

        var h2 = Hash(h);
        var h3 = Hash(h2);
        var jitter = new Vector2(h2 & 0xffffu, h2 >> 16) / 65535f;
        var inCell = new Vector2(CellMargin) + jitter * (1f - 2f * CellMargin);
        direction = Vector3.Normalize(FaceDirection(face, CellPointUv(x + inCell.X, y + inCell.Y, cellsPerFace)));
        brightness = MathUtils.Lerp(WorldGenerationConfig.StarFaintest, 1f,
            MathF.Pow(Unit(h3), WorldGenerationConfig.StarBrightnessCurve));
        return true;
    }

    /// <summary>
    /// The twinkle of the star in cell (<paramref name="face"/>, <paramref name="x"/>, <paramref name="y"/>):
    /// its rate in cycles per second (between <see cref="WorldGenerationConfig.StarTwinkleRateMin"/>
    /// and <see cref="WorldGenerationConfig.StarTwinkleRateMax"/>) and its phase in [0, tau), both
    /// hashed from the cell, so a star keeps its own flicker frame to frame and its neighbours do
    /// not flicker in step. Twin of the rate and phase in sky-fragment.glsl's <c>Stars</c>.
    /// </summary>
    public static (float Rate, float Phase) TwinkleOf(int face, int x, int y)
    {
        // The chain TryGetStar walks (cell, jitter, brightness, tint), two links further on
        var h3 = Hash(Hash(CellHash(face, x, y)));
        var h4 = Hash(Hash(h3));
        return (MathUtils.Lerp(WorldGenerationConfig.StarTwinkleRateMin, WorldGenerationConfig.StarTwinkleRateMax, Unit(h4)),
            Unit(Hash(h4)) * MathF.Tau);
    }

    /// <summary>
    /// A star's brightness multiplier at <paramref name="time"/> seconds: two sines at an irrational
    /// ratio, so the flicker never visibly repeats, smooth everywhere and swinging between
    /// 1 - <see cref="WorldGenerationConfig.StarTwinkleDepth"/> and 1. Twin of sky-fragment.glsl's.
    /// </summary>
    public static float Twinkle(float time, float rate, float phase)
    {
        var a = MathF.Tau * rate * time + phase;
        var w = 0.5f + 0.3f * MathF.Sin(a) + 0.2f * MathF.Sin(2.31f * a + 1.7f * phase);
        return 1f - WorldGenerationConfig.StarTwinkleDepth * w;
    }
}
