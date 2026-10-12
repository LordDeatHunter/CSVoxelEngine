using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Effects.Climate;

/// <summary>
/// One palette's vertex tint over a 16-block square: the tints at its four corners, interpolated
/// bilinearly (<see cref="BiomeTint.Bilinear"/>). <c>C10</c> is the +X corner, <c>C01</c> the +Z.
/// </summary>
public readonly record struct BiomeTintField(Vector3 C00, Vector3 C10, Vector3 C01, Vector3 C11)
{
    /// <summary>The tint at field-local (x, z), each 0..<see cref="Chunk.Size"/>; the far edge
    /// (16) is legal and equals the neighboring field's near edge bit for bit.</summary>
    public Vector3 Sample(float localX, float localZ) =>
        BiomeTint.Bilinear(C00, C10, C01, C11, localX / Chunk.Size, localZ / Chunk.Size);
}
