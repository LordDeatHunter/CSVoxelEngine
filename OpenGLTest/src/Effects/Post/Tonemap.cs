using System.Numerics;

namespace VoxelEngine.Effects.Post;

/// <summary>
/// The C# twin of <c>post-fragment.glsl</c>'s tonemap, used only by tests: exposure, then a
/// per-channel clamp to 0..1, exactly what the pre-HDR 8-bit target did to an over-bright
/// channel. A hue-preserving shoulder kept sunlit snow cream, sand orange and lava red instead
/// of letting them clip toward white and yellow, so it was dropped.
/// </summary>
public static class Tonemap
{
    /// <summary>Scale by <paramref name="exposure"/>, then clamp each channel to 0..1.</summary>
    public static Vector3 Apply(Vector3 hdr, float exposure) =>
        Vector3.Clamp(hdr * exposure, Vector3.Zero, Vector3.One);
}
