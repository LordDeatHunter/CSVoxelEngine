using System.Numerics;

namespace VoxelEngine.Effects.Post;

/// <summary>
/// One expanding cylindrical shell that bends the screen (post-fragment.glsl): everything whose world position lies
/// within a few blocks of <paramref name="Radius"/> from the axis through <paramref name="Center"/>, between
/// <paramref name="BottomY"/> and <paramref name="TopY"/>, is read from a point pushed along the way out.
/// <paramref name="Strength"/> (0..1) fades the lens out as the shell dies.
/// </summary>
public readonly record struct Shockwave(Vector3 Center, float Radius, float BottomY, float TopY, float Strength,
    float HalfThickness)
{
    /// <summary>The most shells the post pass bends at once (the shader's array size).</summary>
    public const int Max = 4;
}
