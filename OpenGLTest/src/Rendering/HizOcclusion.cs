using System;
using System.Numerics;

namespace VoxelEngine.Rendering;

/// <summary>
/// One inclusive pixel rectangle of the frame (or of a <see cref="HizPyramid"/>'s source), in
/// window coordinates: x to the right, y bottom-up, the layout <c>glGetTextureImage</c> returns.
/// </summary>
internal readonly record struct HizRect(int X0, int Y0, int X1, int Y1);

/// <summary>
/// The GL-free occlusion test of a chunk against a max-depth pyramid (<see cref="HizPyramid"/>) built
/// from depth that is already in the frame: whether every fragment the chunk could rasterize lies
/// strictly behind that depth, so drawing it changes no pixel. The C# twin of
/// <c>hiz-cull-compute.glsl</c> (and, through <see cref="HizPyramid"/>, of
/// <c>hiz-build-compute.glsl</c>); keep them in step (see the shader-sync skill).
/// <see cref="ChunkOcclusionCuller"/> runs it on the GPU between the segments of the opaque chunk
/// pass (<see cref="SegmentStarts"/>), against the depth the LOD and the earlier segments wrote.
/// <para>
/// <b>Why a culled chunk writes no fragment.</b> Every vertex a chunk draws lies in its box grown by
/// <see cref="ReachBlocks"/> (see <see cref="GrowAabb"/>). With every corner of that box in front of
/// the eye (<c>w &gt; 0</c>), the perspective projection maps the box onto the convex hull of its
/// projected corners, so a fragment's pixel center lies between the corners' window extremes and the
/// fragment's pixel inside the outward-rounded rect of <see cref="TryScreenBounds"/>. Window depth
/// <c>0.5 z / w + 0.5</c> is a linear-fractional function of the point with a positive denominator, so
/// over the box it is smallest at a corner, and the rasterizer interpolates exactly that depth at a
/// pixel center (depth is affine in screen space over a plane): no fragment of the chunk is nearer
/// than <c>nearDepth</c>. The pyramid's max over the rect is at least the depth stored at every pixel
/// of the rect (<see cref="HizPyramid.MaxOver"/>), and depth in the frame only ever falls, so when
/// <see cref="IsOccluded"/> holds every fragment of the chunk fails <c>LEQUAL</c> against depth that
/// is already written, whatever the blend, alpha, or order: the pixel output is identical.
/// </para>
/// <para>
/// <b>Window depth.</b> <c>Camera.GetProjectionMatrix</c> is System.Numerics (Direct3D-style clip z of
/// 0..w) under GL's default clip control, so window depth is <c>0.5 z / w + 0.5</c>; this compares
/// raw depths and never linearizes. A box with a corner at or behind the eye plane
/// (<c>w &lt;= </c><see cref="MinClipW"/>) is always drawn.
/// </para>
/// Pure functions; nothing is allocated.
/// </summary>
internal static class HizOcclusion
{
    /// <summary>
    /// How far a chunk's drawn opaque vertices reach past its recorded box. In the camera's
    /// <c>CHUNK_DRAW</c> path of <c>test-vertex.glsl</c> only two things move a vertex: the leaf sway
    /// (byte-identical to <c>shadow-vertex.glsl</c>'s, <see cref="ShadowCasterCull.SwayReachBlocks"/>)
    /// and the waves, which only touch fluid quads (alpha 254), and every fluid quad is meshed into the
    /// translucent pool (<c>FluidEmitter</c>), never the opaque one. Add the multiblock overhang and
    /// this is exactly <see cref="ShadowCasterCull.CasterReachBlocks"/>.
    /// </summary>
    internal const float ReachBlocks = ShadowCasterCull.CasterReachBlocks;

    /// <summary>
    /// Texels added on every side of the rounded rect for the float differences between this
    /// projection and the GPU's (vertex transform, clip, viewport transform, snapping).
    /// </summary>
    internal const int SafetyTexels = 1;

    /// <summary>
    /// How far a chunk's nearest depth must lie past the pyramid's max to count as behind it. A
    /// 24-bit depth buffer rounds a stored depth by up to half of one step (1 / (2^24 - 1)); the
    /// GPU's interpolated z/w near 1 and this CPU (or compute) corner projection each carry a few
    /// float ulps (one float ulp just below 1 is about one 24-bit step). Eight steps cover them all
    /// with room to spare, and at chunk scales they cost nothing measurable.
    /// </summary>
    internal const float DepthMargin = 8f / 16777215f;

    /// <summary>A projected corner at or below this clip w is treated as crossing the eye plane.</summary>
    internal const float MinClipW = 1e-4f;

    /// <summary>
    /// Window coordinates are clamped to this many pixels either side of the origin before they are
    /// rounded, so a corner just in front of the eye plane (a huge <c>x / w</c>) never overflows the
    /// integer conversion. Far outside any frame, so the clamp only ever moves a rect edge that the
    /// frame clamp would move anyway.
    /// </summary>
    internal const float ScreenClampPixels = 1 << 20;

    /// <summary>A chunk's box grown by <see cref="ReachBlocks"/> on every axis.</summary>
    internal static void GrowAabb(Vector3 worldMin, Vector3 worldMax, out Vector3 min, out Vector3 max)
    {
        var reach = new Vector3(ReachBlocks);
        min = worldMin - reach;
        max = worldMax + reach;
    }

    /// <summary>
    /// The outward-rounded window rect (clamped to the frame) and the nearest window depth of the box
    /// <paramref name="min"/>..<paramref name="max"/> under <paramref name="viewProj"/>. False, meaning
    /// the box must be drawn, when any corner is at or behind the eye plane or the rect falls wholly
    /// off the frame.
    /// </summary>
    internal static bool TryScreenBounds(in Matrix4x4 viewProj, Vector3 min, Vector3 max, int width, int height,
        out HizRect rect, out float nearDepth)
    {
        rect = default;
        nearDepth = 0f;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        var near = float.MaxValue;
        for (var k = 0; k < 8; k++)
        {
            var p = new Vector3((k & 1) != 0 ? max.X : min.X, (k & 2) != 0 ? max.Y : min.Y, (k & 4) != 0 ? max.Z : min.Z);
            var c = Vector4.Transform(new Vector4(p, 1f), viewProj);
            if (!(c.W > MinClipW)) return false;
            var sx = Math.Clamp((c.X / c.W * 0.5f + 0.5f) * width, -ScreenClampPixels, ScreenClampPixels);
            var sy = Math.Clamp((c.Y / c.W * 0.5f + 0.5f) * height, -ScreenClampPixels, ScreenClampPixels);
            minX = MathF.Min(minX, sx);
            maxX = MathF.Max(maxX, sx);
            minY = MathF.Min(minY, sy);
            maxY = MathF.Max(maxY, sy);
            near = MathF.Min(near, 0.5f * c.Z / c.W + 0.5f);
        }

        var x0 = (int)MathF.Floor(minX) - SafetyTexels;
        var x1 = (int)MathF.Floor(maxX) + SafetyTexels;
        var y0 = (int)MathF.Floor(minY) - SafetyTexels;
        var y1 = (int)MathF.Floor(maxY) + SafetyTexels;
        if (x1 < 0 || y1 < 0 || x0 > width - 1 || y0 > height - 1) return false;

        rect = new HizRect(Math.Max(0, x0), Math.Max(0, y0), Math.Min(width - 1, x1), Math.Min(height - 1, y1));
        nearDepth = near;
        return true;
    }

    /// <summary>Whether a box whose nearest depth is <paramref name="nearDepth"/> lies strictly behind
    /// <paramref name="hizMax"/>, past <see cref="DepthMargin"/>. A tie is never occluded.</summary>
    internal static bool IsOccluded(float nearDepth, float hizMax) => nearDepth > hizMax + DepthMargin;

    /// <summary>
    /// Where a front-to-back run of <paramref name="count"/> draws is split for the culls: the start of
    /// every segment after the first, at the share of the run each of <paramref name="fractions"/>
    /// names (ascending, each in 0..1), written to <paramref name="starts"/> strictly increasing and
    /// inside <c>(0, count)</c>. A fraction that would leave a segment empty is skipped. Returns how
    /// many starts it wrote; with none the run is drawn whole.
    /// </summary>
    internal static int SegmentStarts(int count, ReadOnlySpan<float> fractions, Span<int> starts)
    {
        var written = 0;
        var previous = 0;
        foreach (var fraction in fractions)
        {
            var start = (int)(count * fraction);
            if (start <= previous || start >= count) continue;
            starts[written++] = start;
            previous = start;
        }

        return written;
    }
}
