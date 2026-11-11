using System;
using System.Numerics;

namespace VoxelEngine.Effects.Surfaces;

/// <summary>
/// GL-free twin of the forcefield term in test-fragment.glsl (<c>ForcefieldHexEdge</c>, <c>ForcefieldAlpha</c>): the battle
/// tower's roof dome draws in the translucent pass as a faint film, its own color lit like an albedo, whose alpha replaces
/// the tile's. A bare film,
/// a rim toward a graze, a honeycomb that shows only in patches drifting on the shader's value noise and a soft band
/// forever rising up the dome; the honeycomb and the band fade out with distance, so far faces never alias. The weights are
/// <see cref="SurfaceShadingConfig"/>'s Forcefield constants, which <c>RenderManager</c> uploads; the shape literals (the
/// lattice, the edge width, the flicker and band rates) are written in both. Float math, so it follows the shader closely
/// but not to the last bit.
/// </summary>
public static class ForcefieldShimmer
{
    private static readonly Vector2 Lattice = new(1f, 1.7320508f);
    private static readonly Vector2 EdgeNormal = new(0.5f, 0.8660254f);

    /// <summary>The honeycomb at <paramref name="p"/> in lattice units (cell centers one unit apart on a triangular lattice):
    /// 0 inside a cell, rising to 1 on its edge.</summary>
    public static float HexEdge(Vector2 p)
    {
        var a = Mod(p, Lattice) - 0.5f * Lattice;
        var b = Mod(p - 0.5f * Lattice, Lattice) - 0.5f * Lattice;
        // The offset from the nearest cell center, and its hexagonal distance: 0.5 on an edge
        var q = Vector2.Abs(Vector2.Dot(a, a) < Vector2.Dot(b, b) ? a : b);
        return MathUtils.SmoothStep(0.42f, 0.5f, MathF.Max(q.X, Vector2.Dot(q, EdgeNormal)));
    }

    /// <summary>
    /// The film's alpha on a face point at <paramref name="world"/> (<paramref name="face"/> its coordinates along the
    /// face's tangent and bitangent) seen at <paramref name="cosView"/> (V.N, from either side) from
    /// <paramref name="distance"/> blocks at time <paramref name="time"/>, scaled by <paramref name="strength"/> and clamped
    /// to 0..1.
    /// </summary>
    public static float Alpha(Vector2 face, Vector3 world, float cosView, float time, float distance, float strength)
    {
        var graze = 1f - MathF.Abs(cosView);
        var film = SurfaceShadingConfig.ForcefieldFilmAlpha + SurfaceShadingConfig.ForcefieldRimAlpha * graze * graze * graze;
        var detail = 1f - MathUtils.SmoothStep(SurfaceShadingConfig.ForcefieldDetailFadeNearBlocks,
            SurfaceShadingConfig.ForcefieldDetailFadeFarBlocks, distance);
        var edge = HexEdge(face * SurfaceShadingConfig.ForcefieldHexCellsPerBlock);
        var flicker = LavaPulse.ValueNoise(new Vector2(world.X, world.Z) * 0.35f + new Vector2(time * 0.23f, -time * 0.17f)
                                           + new Vector2(world.Y * 0.5f));
        var sparkle = SurfaceShadingConfig.ForcefieldHexAlpha * edge * MathUtils.SmoothStep(0.4f, 0.8f, flicker)
                      + SurfaceShadingConfig.ForcefieldBandAlpha * Band(world.Y, time) * (0.5f + 0.5f * edge);
        return Math.Clamp(strength * (film + detail * sparkle), 0f, 1f);
    }

    // The rising band: a narrow crest every 2 pi / 0.45 (about 14) blocks of height, climbing 1.3 / 0.45 (about 2.9)
    // blocks a second; 0..1
    private static float Band(float y, float time)
    {
        var s = 0.5f + 0.5f * MathF.Sin(y * 0.45f - time * 1.3f);
        s *= s;
        s *= s;
        return s * s;
    }

    // GLSL mod: always in 0..m
    private static Vector2 Mod(Vector2 v, Vector2 m) =>
        new(v.X - m.X * MathF.Floor(v.X / m.X), v.Y - m.Y * MathF.Floor(v.Y / m.Y));
}
