using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// The mining pop's geometry: each hitbox drawn as a box sampling its crop of the block's
/// face tiles at full-texel density. Scaling a full-cube mesh into a thin hitbox stretches
/// its texture (what snow slabs showed); emitting the box with the same corner order, UVs
/// and shade lookup as the chunk mesher (<see cref="Chunk.BoxFaceCorners"/>,
/// <see cref="Chunk.CropUv"/>) makes the pop pixel-identical to the terrain it jumps out of.
/// Tile-based like the other procedural emitters (LOD, pebbles, tree wood), so every
/// partial block is covered with no per-renderer branches: a new one only needs sensible
/// <c>FaceTiles</c> (the baker falls back to the first quad's tile). A biome-tinted block's
/// pop wears its <see cref="BiomeTintFaces"/> tiles instead (a base quad, then the tinted quad
/// flagged by Vertex bit 4) and takes its tint from RenderManager's <c>uBiomeTint</c> uniform
/// (the field at the mined cell), so it keeps matching the terrain it jumps out of; the
/// vertex rgb stays the shade alone. GL-free; the caller streams the lists into its own
/// dynamic buffers.
/// </summary>
public static class PopBoxEmitter
{
    /// <summary>
    /// The most quads one box can emit: six faces, each at most a base and a tinted quad.
    /// RenderManager sizes the pop's streaming VBO and its static index pattern by it.
    /// </summary>
    public const int MaxQuadsPerBox = 12;

    /// <summary>
    /// Appends the six faces of <paramref name="grown"/> (cell-space grown corners) offset by
    /// <paramref name="blockPosition"/>, each face sampling <paramref name="crop"/>'s footprint
    /// of <paramref name="faceTiles"/>[face], or of the <paramref name="biomeTint"/> tiles when
    /// the block has them. Shaded fully unoccluded like the other standalone copies (drops,
    /// icons).
    /// </summary>
    public static void EmitBox(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        BlockHitbox grown, BlockHitbox crop, Vector3 blockPosition, ushort[] faceTiles,
        BiomeTintFaces? biomeTint = null)
    {
        for (var face = 0; face < 6; face++)
        {
            Chunk.BoxFaceCorners(grown.Min, grown.Max, face, out var a, out var b, out var c, out var d);
            Chunk.BoxFaceCorners(crop.Min, crop.Max, face, out var ta, out var tb, out var tc, out var td);
            var shade = Chunk.ShadeLut[(face << 2) | 3];
            var normal = Chunk.CubeFaces[face].normal;

            if (biomeTint is { } bt)
            {
                // Base first, tinted second: the coplanar overlay wins under Lequal by draw order
                if (bt.Base[face] != BiomeTintFaces.NoTile)
                    Emit(vertices, indices, ref indexOffset, bt.Base[face], false);
                if (bt.Tinted[face] != BiomeTintFaces.NoTile)
                    Emit(vertices, indices, ref indexOffset, bt.Tinted[face], true);
            }
            else
            {
                Emit(vertices, indices, ref indexOffset, faceTiles[face], false);
            }

            void Emit(List<Vertex> vs, List<ushort> ids, ref ushort offset, ushort tile, bool tinted)
            {
                vs.Add(new Vertex(blockPosition + a, shade, shade, shade, 255, Chunk.CropUv(face, ta), tile, 1f, 0f, normal, biomeTint: tinted));
                vs.Add(new Vertex(blockPosition + b, shade, shade, shade, 255, Chunk.CropUv(face, tb), tile, 1f, 0f, normal, biomeTint: tinted));
                vs.Add(new Vertex(blockPosition + c, shade, shade, shade, 255, Chunk.CropUv(face, tc), tile, 1f, 0f, normal, biomeTint: tinted));
                vs.Add(new Vertex(blockPosition + d, shade, shade, shade, 255, Chunk.CropUv(face, td), tile, 1f, 0f, normal, biomeTint: tinted));
                ids.Add((ushort)(offset + 0));
                ids.Add((ushort)(offset + 1));
                ids.Add((ushort)(offset + 2));
                ids.Add((ushort)(offset + 0));
                ids.Add((ushort)(offset + 2));
                ids.Add((ushort)(offset + 3));
                offset += 4;
            }
        }
    }
}
