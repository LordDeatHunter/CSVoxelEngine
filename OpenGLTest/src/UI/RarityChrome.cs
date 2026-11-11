using System;
using System.Numerics;
using static VoxelEngine.Config.RarityStyleConfig;

namespace VoxelEngine.UI;

/// <summary>
/// The GL-free twin of <c>ui-fragment.glsl</c>'s rarity chrome (<c>uChrome</c> 1): the dressing a
/// <see cref="RarityStyle"/> lays over a list row or the hover tooltip box. <c>uv</c> runs 0..1 across the box
/// with y down, <c>p = uv * size</c> is the same point in canvas units. The layers are a rarity-colored border
/// band and inner glow that breathe by the pulse (<see cref="Border"/>, <see cref="Glow"/>, <see cref="Pulse"/>),
/// a wash fading in from the row's left or the tooltip's top (<see cref="Wash"/>), a slanted white sheen that
/// sweeps across once a period and rests between sweeps (<see cref="Sheen"/>, <see cref="MathUtils.Sweep"/>), and
/// hashed four-point sparkles that twinkle (<see cref="Sparkle"/>).
/// <para>
/// <see cref="For"/> packs exactly what <c>RarityChromeRenderer</c> uploads as the <c>uChrome*</c> uniforms; the
/// time curves run here on the CPU and the shader only does the spatial work. It twins the formula, not the
/// GPU's bits: GPU <c>sin</c> is not bit-exact with <see cref="MathF.Sin(float)"/>, so a sparkle's cell and spot
/// can differ slightly, which is why the tests assert relations only. Change the shader and this class together
/// (shader-sync skill).
/// </para>
/// </summary>
public static class RarityChrome
{
    /// <summary>
    /// The chrome's uniforms, one field per <c>uChrome*</c> vec4 of ui-fragment.glsl (sizes in canvas units).
    /// </summary>
    /// <param name="Box">uChromeBox: (size x, size y, sheen slant, sparkle density).</param>
    /// <param name="Color">uChromeColor: (r, g, b, opacity).</param>
    /// <param name="Layers">uChromeLayers: (wash, glow, sheen (0 while it rests), sparkle).</param>
    /// <param name="Shape">uChromeShape: (border width, glow reach, sheen half-width, sparkle cell size).</param>
    /// <param name="Motion">uChromeMotion: (sheen center along <c>p.x + slant * p.y</c>, pulse factor, twinkle
    /// phase 0..1, wash lead: 0 from the left edge, 1 from the top edge).</param>
    public readonly record struct Inputs(Vector4 Box, Vector4 Color, Vector4 Layers, Vector4 Shape, Vector4 Motion);

    /// <summary>
    /// The uniforms for <paramref name="style"/>'s chrome over a box of <paramref name="size"/> canvas units at
    /// <paramref name="time"/> seconds: the sheen's place in its sweep (off the box while it rests), the pulse
    /// and the twinkle phase. <paramref name="opacity"/> scales the style's own (a dimmed row).
    /// </summary>
    public static Inputs For(RarityStyle style, Vector2 size, float time, float borderWidth, bool washFromTop,
        float opacity = 1f)
    {
        var sweep = MathUtils.Sweep(time, SheenPeriodSeconds, SheenSweepShare);
        // The band starts and ends a half-width off the box, so it enters and leaves without a pop.
        var span = size.X + SheenSlant * size.Y + 2f * SheenHalfWidth;
        var center = -SheenHalfWidth + (sweep ?? 0f) * span;
        var c = style.Color.ToVector4();
        return new Inputs(
            new Vector4(size.X, size.Y, SheenSlant, SparkleDensity),
            new Vector4(c.X, c.Y, c.Z, style.Opacity * opacity),
            new Vector4(style.Wash, style.Glow, sweep.HasValue ? style.Sheen : 0f, style.Sparkle),
            new Vector4(borderWidth, GlowReach, SheenHalfWidth, SparkleCellSize),
            new Vector4(center, Pulse(time, style.PulseDepth), Fract(time * SparkleTwinkleHz), washFromTop ? 1f : 0f));
    }

    /// <summary>The breath the border and glow are scaled by: between <c>1 - depth</c> and 1, once a
    /// <see cref="RarityStyleConfig.PulsePeriodSeconds"/>; always 1 for a depth of 0.</summary>
    public static float Pulse(float time, float depth) =>
        1f - depth * (0.5f - 0.5f * MathUtils.Oscillate(time, PulsePeriodSeconds));

    /// <summary>The sine hash of a sparkle cell: a pseudo-random fraction in [0, 1).</summary>
    public static float Hash(Vector2 cell) =>
        Fract(MathF.Sin(Vector2.Dot(cell, new Vector2(127.1f, 311.7f))) * 43758.5453f);

    /// <summary>How far <paramref name="p"/> is from the box's nearest edge, in canvas units.</summary>
    public static float Edge(Vector2 p, in Inputs c)
    {
        var nearest = Vector2.Min(p, new Vector2(c.Box.X, c.Box.Y) - p);
        return MathF.Min(nearest.X, nearest.Y);
    }

    /// <summary>The border band: 1 inside the border width from the edge, a half-unit soft step out of it.</summary>
    public static float Border(float edge, in Inputs c) =>
        1f - MathUtils.SmoothStep(c.Shape.X - 0.5f, c.Shape.X + 0.5f, edge);

    /// <summary>The inner glow: 1 on the edge, falling off exponentially over the glow reach.</summary>
    public static float Glow(float edge, in Inputs c) => MathF.Exp(-edge / c.Shape.Y);

    /// <summary>The wash: strongest at the leading edge (the left, or the top when the lead is 1), quadratic to 0
    /// at the far one.</summary>
    public static float Wash(Vector2 uv, in Inputs c)
    {
        var t = 1f - MathUtils.LerpUnclamped(uv.X, uv.Y, c.Motion.W);
        return t * t;
    }

    /// <summary>The sheen band: 1 on its slanted center line, a smooth fall to 0 a half-width away.</summary>
    public static float Sheen(Vector2 p, in Inputs c) =>
        1f - MathUtils.SmoothStep(0f, c.Shape.Z, MathF.Abs(p.X + c.Box.Z * p.Y - c.Motion.X));

    /// <summary>
    /// The sparkle at <paramref name="p"/>: a star sits at a hashed spot in the middle half of a share of the cells
    /// (the density), a soft core with a long cross of arms, its brightness twinkling with the phase. The hash of a
    /// star's cell is below the density, so it is rescaled to a 0..1 fraction first: its spot and its twinkle's
    /// phase offset then spread evenly whatever the density, and the stars twinkle out of step.
    /// </summary>
    public static float Sparkle(Vector2 p, in Inputs c)
    {
        var q = p / c.Shape.W;
        var cell = new Vector2(MathF.Floor(q.X), MathF.Floor(q.Y));
        var h = Hash(cell);
        if (h >= c.Box.W) return 0f;
        var u = h / c.Box.W;
        var spot = new Vector2(0.25f) + 0.5f * new Vector2(Fract(u * 17f), Fract(u * 31f));
        var d = Vector2.Abs(q - cell - spot);
        var core = MathF.Max(0f, 1f - d.Length() * 5f);
        var arms = MathF.Max(0f, 1f - d.X * 12f) * MathF.Max(0f, 1f - d.Y * 2.5f)
                   + MathF.Max(0f, 1f - d.Y * 12f) * MathF.Max(0f, 1f - d.X * 2.5f);
        var twinkle = MathF.Pow(MathF.Max(0f, 0.5f + 0.5f * MathF.Sin(MathF.Tau * (c.Motion.Z + u))), 6f);
        return MathF.Min(1f, core + arms) * twinkle;
    }

    /// <summary>
    /// The chrome's straight-alpha color at <paramref name="uv"/> before the panel fade: the rarity-colored tint
    /// (the brighter of the wash and the pulsing rim) under the white light of the sheen and sparkles, its alpha
    /// scaled by the chrome's opacity.
    /// </summary>
    public static Vector4 Shade(Vector2 uv, in Inputs c)
    {
        var p = uv * new Vector2(c.Box.X, c.Box.Y);
        var edge = Edge(p, c);
        var rim = MathF.Max(Border(edge, c), c.Layers.Y * Glow(edge, c)) * c.Motion.Y;
        var tint = MathF.Max(c.Layers.X * Wash(uv, c), rim);
        var light = MathUtils.Clamp01(c.Layers.Z * Sheen(p, c) + c.Layers.W * Sparkle(p, c));
        var alpha = light + tint * (1f - light);
        var rgb = alpha > 0f
            ? (new Vector3(light) + new Vector3(c.Color.X, c.Color.Y, c.Color.Z) * tint * (1f - light)) / alpha
            : Vector3.Zero;
        return new Vector4(rgb, alpha * c.Color.W);
    }

    private static float Fract(float x) => x - MathF.Floor(x);
}
