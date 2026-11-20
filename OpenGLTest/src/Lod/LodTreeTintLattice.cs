using System;
using System.Numerics;
using VoxelEngine.Effects.Climate;

namespace VoxelEngine.Lod;

/// <summary>
/// Build-local copy of the existing 16-block grass/foliage corner lattice. Each vec4
/// occupies 16 std430 bytes; W is reserved zero. Inputs are explicit source-derived
/// vertex multipliers, never fresh climate or ColumnCache reads inside this helper.
/// </summary>
internal sealed class LodTreeTintLattice
{
    private readonly Vector4[] _grass;
    private readonly Vector4[] _foliage;
    internal int ChunkSpan { get; }
    internal int LatticeSide => ChunkSpan + 1;
    internal int ByteSize => checked(2 * LatticeSide * LatticeSide * 16);

    internal LodTreeTintLattice(int chunkSpan, ReadOnlySpan<Vector3> grass, ReadOnlySpan<Vector3> foliage)
    {
        if (chunkSpan < 1 || chunkSpan > 16)
            throw new ArgumentOutOfRangeException(nameof(chunkSpan));
        var side = chunkSpan + 1;
        if (grass.Length != side * side || foliage.Length != side * side)
            throw new ArgumentException("Each tint palette needs every shared lattice corner.");
        ChunkSpan = chunkSpan;
        _grass = new Vector4[grass.Length];
        _foliage = new Vector4[foliage.Length];
        for (var i = 0; i < grass.Length; i++)
        {
            _grass[i] = new Vector4(grass[i], 0f);
            _foliage[i] = new Vector4(foliage[i], 0f);
        }
    }

    internal ReadOnlySpan<Vector4> Grass => _grass;
    internal ReadOnlySpan<Vector4> Foliage => _foliage;

    internal Vector3 Sample(BiomeTintPalette palette, float localX, float localZ)
    {
        var grid = palette == BiomeTintPalette.Foliage ? _foliage : _grass;
        if (localX < 0 || localZ < 0 || localX > ChunkSpan * 16 || localZ > ChunkSpan * 16)
            throw new ArgumentOutOfRangeException(nameof(localX));
        var cx = Math.Clamp((int)localX >> 4, 0, ChunkSpan - 1);
        var cz = Math.Clamp((int)localZ >> 4, 0, ChunkSpan - 1);
        var fx = localX / 16f - cx;
        var fz = localZ / 16f - cz;
        var side = LatticeSide;
        return BiomeTint.Bilinear(
            ToRgb(grid[cx * side + cz]), ToRgb(grid[(cx + 1) * side + cz]),
            ToRgb(grid[cx * side + cz + 1]), ToRgb(grid[(cx + 1) * side + cz + 1]), fx, fz);
    }

    private static Vector3 ToRgb(Vector4 value) => new(value.X, value.Y, value.Z);
}
