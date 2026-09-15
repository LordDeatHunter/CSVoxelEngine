using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// The mining pop's geometry: each hitbox drawn as a box sampling its crop of the block's
/// face tiles at full-texel density. Scaling a full-cube mesh into a thin hitbox stretches
/// its texture (what snow slabs showed); emitting the box with the same corner order, UVs
/// and shade lookup as the chunk mesher (<see cref="Chunk.BoxFaceCorners"/>,
/// <see cref="Chunk.CropUv"/>) makes the pop pixel-identical to the terrain it jumps out of.
/// Tile-based like the other procedural emitters (LOD, pebbles, tapered wood), so every
/// partial block is covered with no per-renderer branches: a new one only needs sensible
/// <c>FaceTiles</c> (the baker falls back to the first quad's tile). GL-free; the caller
/// streams the lists into its own dynamic buffers.
/// </summary>
public static class PopBoxEmitter
{
    /// <summary>
    /// Appends the six faces of <paramref name="grown"/> (cell-space grown corners) offset by
    /// <paramref name="blockPosition"/>, each face sampling <paramref name="crop"/>'s footprint
    /// of <paramref name="faceTiles"/>[face]. Shaded fully unoccluded like the other standalone
    /// copies (drops, icons).
    /// </summary>
    public static void EmitBox(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        BlockHitbox grown, BlockHitbox crop, Vector3 blockPosition, ushort[] faceTiles)
    {
        for (var face = 0; face < 6; face++)
        {
            Chunk.BoxFaceCorners(grown.Min, grown.Max, face, out var a, out var b, out var c, out var d);
            Chunk.BoxFaceCorners(crop.Min, crop.Max, face, out var ta, out var tb, out var tc, out var td);
            var tile = faceTiles[face];
            var shade = Chunk.ShadeLut[(face << 2) | 3];
            var normal = Chunk.CubeFaces[face].normal;
            vertices.Add(new Vertex(blockPosition + a, shade, shade, shade, 255, Chunk.CropUv(face, ta), tile, 1f, 0f, normal));
            vertices.Add(new Vertex(blockPosition + b, shade, shade, shade, 255, Chunk.CropUv(face, tb), tile, 1f, 0f, normal));
            vertices.Add(new Vertex(blockPosition + c, shade, shade, shade, 255, Chunk.CropUv(face, tc), tile, 1f, 0f, normal));
            vertices.Add(new Vertex(blockPosition + d, shade, shade, shade, 255, Chunk.CropUv(face, td), tile, 1f, 0f, normal));
            indices.Add((ushort)(indexOffset + 0));
            indices.Add((ushort)(indexOffset + 1));
            indices.Add((ushort)(indexOffset + 2));
            indices.Add((ushort)(indexOffset + 0));
            indices.Add((ushort)(indexOffset + 2));
            indices.Add((ushort)(indexOffset + 3));
            indexOffset += 4;
        }
    }
}
