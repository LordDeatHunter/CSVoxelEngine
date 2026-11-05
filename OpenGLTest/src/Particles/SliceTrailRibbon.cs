using System;
using System.Numerics;
using VoxelEngine.Data;

namespace VoxelEngine.Particles;

/// <summary>
/// Turns a <see cref="SliceTrail"/> into the slice-trail ribbon: quads in the shared <see cref="Vertex"/>
/// layout, ready for <c>SliceTrailRenderer</c>'s static 0,1,2 / 0,2,3 index pattern. GL-free and
/// allocation-free.
/// <para>
/// Each run of samples with one segment id (one swing) is smoothed with a uniform Catmull-Rom curve,
/// <see cref="ParticleConfig.SliceTrailSubdivisions"/> cross-sections per gap, and two runs are never
/// bridged. A cross-section spans from the tip toward the inner blade point by its width: the old end
/// tapers by its position along the run (<see cref="ParticleConfig.SliceTrailTailTaper"/>), the new
/// end by the tip's speed (the sample's strength), so a finished swing is a crescent thin at both
/// ends, and the whole ribbon narrows (<see cref="ParticleConfig.SliceTrailAgeShrink"/>) and fades
/// (alpha <c>(1 - age)^2</c>) with age.
/// </para>
/// <para>
/// UV.x is the sample's stable arc length in blocks (<see cref="SliceTrail.AlongAt"/>), which the
/// fragment shader keys its streaks on, so the wisps stay put on the arc as the tail expires; UV.y is
/// 0 at the inner edge and 1 at the tip edge.
/// </para>
/// </summary>
public static class SliceTrailRibbon
{
    /// <summary>The most quads one trail builds: every gap of a full ring plus the live head's.</summary>
    public const int MaxQuads = ParticleConfig.SliceTrailMaxSamples * ParticleConfig.SliceTrailSubdivisions;

    /// <summary>The most vertices one trail builds; must stay at or below 65535 for the ushort index buffer.</summary>
    public const int MaxVertices = MaxQuads * 4;

    // One cross-section of the ribbon: its world-space tip and drawn inner edge, its streak coordinate,
    // alpha and width
    private readonly record struct Section(Vector3 Tip, Vector3 Inner, float Along, byte Alpha, float Width)
    {
        public bool Degenerate => Alpha == 0 || Width <= 0f;
    }

    /// <summary>
    /// Writes the ribbon of <paramref name="trail"/> as it stands at world time <paramref name="now"/>
    /// into <paramref name="dest"/>, and returns the vertex count (a multiple of 4, four per quad). Stops
    /// when <paramref name="dest"/> is full.
    /// </summary>
    public static int Build(SliceTrail trail, double now, Span<Vertex> dest)
    {
        var count = trail.Count;
        var written = 0;
        var start = 0;
        while (start < count)
        {
            var end = RunEnd(trail, start, count);
            if (end - start >= 2)
                written = BuildRun(trail, start, end - start, now, dest, written);
            start = end;
        }
        return written;
    }

    // The exclusive end of the run of equal segment ids starting at start
    private static int RunEnd(SliceTrail trail, int start, int count)
    {
        var segment = trail.SegmentAt(start);
        var end = start + 1;
        while (end < count && trail.SegmentAt(end) == segment)
            end++;
        return end;
    }

    // Emits the quads of one run of length samples starting at start, after the written vertices
    private static int BuildRun(SliceTrail trail, int start, int length, double now, Span<Vertex> dest, int written)
    {
        var color = trail.Color;
        var frame = trail.OwnerFrame;
        var prev = CrossSection(trail, start, length, 0, 0f, now, frame);
        for (var gap = 0; gap < length - 1; gap++)
        {
            for (var k = 1; k <= ParticleConfig.SliceTrailSubdivisions; k++)
            {
                var next = CrossSection(trail, start, length, gap, k / (float)ParticleConfig.SliceTrailSubdivisions,
                    now, frame);
                if (!(prev.Degenerate && next.Degenerate))
                {
                    if (written + 4 > dest.Length)
                        return written;
                    EmitQuad(dest, written, prev, next, color);
                    written += 4;
                }
                prev = next;
            }
        }
        return written;
    }

    private static void EmitQuad(Span<Vertex> dest, int at, in Section a, in Section b, Rgba color)
    {
        dest[at] = Corner(a.Inner, a.Along, 0f, a.Alpha, color);
        dest[at + 1] = Corner(a.Tip, a.Along, 1f, a.Alpha, color);
        dest[at + 2] = Corner(b.Tip, b.Along, 1f, b.Alpha, color);
        dest[at + 3] = Corner(b.Inner, b.Along, 0f, b.Alpha, color);
    }

    private static Vertex Corner(Vector3 position, float along, float v, byte alpha, Rgba color) =>
        new(position, color.R, color.G, color.B, alpha, new Vector2(along, v), 0f, 0f, Vector3.UnitY);

    // The cross-section at fraction t of the gap from run sample gap to gap + 1
    private static Section CrossSection(SliceTrail trail, int start, int length, int gap, float t, double now,
        in Matrix4x4 frame)
    {
        var i1 = start + gap;
        var i2 = i1 + 1;
        var i0 = Math.Max(start, i1 - 1);
        var i3 = Math.Min(start + length - 1, i2 + 1);

        var tip = CatmullRom(trail.TipAt(i0), trail.TipAt(i1), trail.TipAt(i2), trail.TipAt(i3), t);
        var inner = CatmullRom(trail.InnerAt(i0), trail.InnerAt(i1), trail.InnerAt(i2), trail.InnerAt(i3), t);
        var time = trail.TimeAt(i1) + (trail.TimeAt(i2) - trail.TimeAt(i1)) * t;
        var strength = MathUtils.Lerp(trail.StrengthAt(i1), trail.StrengthAt(i2), t);
        var along = MathUtils.Lerp(trail.AlongAt(i1), trail.AlongAt(i2), t);

        var s = (gap + t) / (length - 1);
        var taper = MathUtils.SmoothStep(0f, ParticleConfig.SliceTrailTailTaper, s) * strength;
        var age = MathUtils.Clamp01((float)((now - time) / ParticleConfig.SliceTrailLifeSeconds));
        var width = taper * (1f - ParticleConfig.SliceTrailAgeShrink * age);
        var fade = (1f - age) * (1f - age);
        var alpha = (byte)(MathUtils.Clamp01(fade) * 255f + 0.5f);

        var drawnInner = Vector3.Lerp(tip, inner, width);
        return new Section(Vector3.Transform(tip, frame), Vector3.Transform(drawnInner, frame), along, alpha, width);
    }

    // Uniform Catmull-Rom between p1 (t = 0) and p2 (t = 1)
    private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        var t2 = t * t;
        var t3 = t2 * t;
        return 0.5f * (2f * p1 + (p2 - p0) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
            + (3f * p1 - p0 - 3f * p2 + p3) * t3);
    }
}
