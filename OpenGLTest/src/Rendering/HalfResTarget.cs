using System;

namespace VoxelEngine.Rendering;

/// <summary>
/// The GL-free sizing arithmetic behind the half-resolution underwater-rays render target: given a
/// viewport size, how big is the half-res target, and does a resize actually force the GL objects to
/// be torn down and re-created?
///
/// The class is deliberately dependency-free — <c>System</c> only, no GL, no Silk.NET types, no
/// <c>RenderManager</c> — for the same reason <c>ReflectionReuse</c> is: <c>RenderManager</c> needs a
/// live GL context to construct, so the only way to pin this resize arithmetic headlessly is to keep
/// it out here. The GL half (texture allocation, framebuffer completeness) is not covered by tests at
/// all; it is only reachable by running the game.
///
/// Two properties are worth stating because they are what make the caller's UV math and its resize
/// handling correct:
///
/// <list type="bullet">
/// <item>The half-res target is drawn with the same full-screen triangle covering the same NDC
/// region as the full-res pass, and it is sampled back with the same screen UV. So screen UV maps
/// onto the half-res target as the identity — there is no UV scale factor and no partial-texel edge
/// case. The ceil rounding below therefore changes sample <em>density</em> only, never the UV
/// mapping.</item>
/// <item>Two adjacent viewport widths can share one half-res size (1281 and 1282 both halve to 641).
/// <see cref="ShouldRecreate"/> compares the resulting size rather than the viewport, so a
/// drag-resize does not churn GL objects on every pixel of drag.</item>
/// </list>
/// </summary>
internal static class HalfResTarget
{
    /// <summary>
    /// Smallest half-res dimension on either axis. Matches the <c>Math.Max(2, ...)</c> floor every
    /// other render target in <c>RenderManager</c> uses (see CreateReflectionTarget /
    /// CreateSceneCopy), so a minimized or degenerate window never asks GL for a 0-sized texture.
    /// </summary>
    internal const int MinDimension = 2;

    /// <summary>Pixel dimensions of the half-res target.</summary>
    internal readonly record struct Size(int Width, int Height);

    /// <summary>The half-res size that a viewport of the given dimensions wants.</summary>
    internal static Size For(int viewportWidth, int viewportHeight) =>
        new(Half(viewportWidth), Half(viewportHeight));

    /// <summary>
    /// True when the GL objects must be deleted and re-created: nothing is allocated yet, or the
    /// desired size differs from the one currently allocated.
    /// </summary>
    /// <param name="allocated">A half-res target currently exists.</param>
    /// <param name="current">Size of the currently allocated target (meaningless when not allocated).</param>
    /// <param name="desired">Size the current viewport wants, from <see cref="For"/>.</param>
    internal static bool ShouldRecreate(bool allocated, in Size current, in Size desired) =>
        !allocated || current != desired;

    // Ceil-divide: at an odd viewport width the half-res grid still covers the last column (640.5
    // rounds to 641, not 640), so the upsample never reads past the marched region. Negative and
    // zero inputs are clamped away before the divide so the floor below is the only special case.
    private static int Half(int v) => Math.Max(MinDimension, (Math.Max(0, v) + 1) / 2);
}
