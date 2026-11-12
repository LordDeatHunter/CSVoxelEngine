using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks.Meshing;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Models;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Data;

namespace VoxelEngine.Particles;

/// <summary>
/// The mining pop's geometry: each hitbox drawn as a box sampling its crop of the block's
/// face tiles at full-texel density. Scaling a full-cube mesh into a thin hitbox stretches
/// its texture (what snow slabs showed); emitting the box with the same corner order, UVs
/// and shade lookup as the chunk mesher (<see cref="ChunkFaceTables.BoxFaceCorners"/>,
/// <see cref="ChunkFaceTables.CropUv"/>) makes the pop pixel-identical to the terrain it jumps out of.
/// Tile-based like the other procedural emitters (LOD, pebbles, tree wood), so every
/// partial block is covered with no per-renderer branches: a new one only needs sensible
/// <c>FaceTiles</c> (the baker falls back to the first quad's tile). A biome-tinted block's
/// pop wears its <see cref="BiomeTintFaces"/> tiles instead (a base quad, then the tinted quad
/// flagged by Vertex bit 4) and takes its tint from the <c>uBiomeTint</c> uniform
/// <c>SelectionPasses.RenderBlockPop</c> sets (the field at the mined cell), so it keeps matching
/// the terrain it jumps out of; the vertex rgb stays the shade alone. GL-free; the caller streams
/// the lists into its own dynamic buffers.
/// </summary>
public static class PopBoxEmitter
{
    /// <summary>
    /// The most quads one box can emit: six faces, each at most a base and a tinted quad.
    /// SelectionPasses sizes the pop's streaming VBO and its static index pattern by it.
    /// </summary>
    public const int MaxQuadsPerBox = 12;

    /// <summary>
    /// The tile a tree-wood box wears on <paramref name="face"/> when its grain runs along
    /// <paramref name="capAxis"/> (0 = X, 1 = Y, 2 = Z): the end-grain tiles (top on the positive
    /// face, bottom on the negative) on the two faces across that axis, the bark side tile on
    /// the rest, as <c>TreeWoodEmitter</c> draws a limb's tube.
    /// </summary>
    /// <summary>
    /// Appends a facing block's baked model (already turned to its facing, see
    /// <c>BlockType.RotatedModels</c>) offset by <paramref name="blockPosition"/> and grown about
    /// the cell center by <paramref name="grow"/>, with the model's own tiles and UVs, so a
    /// hollow log pops with the hole and the grain the chunk mesh shows rather than as a plain cube.
    /// </summary>
    public static void EmitModel(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        BakedModel model, float grow, Vector3 blockPosition)
    {
        var center = new Vector3(0.5f);
        foreach (var quad in model.Quads)
        {
            var shade = ChunkFaceTables.ShadeLut[(quad.Face << 2) | 3];
            for (var j = 0; j < 4; j++)
            {
                var p = center + (quad.Positions[j] - center) * (1f + grow);
                vertices.Add(new Vertex(blockPosition + p, shade, shade, shade, 255, quad.Uvs[j], quad.Tile, 1f, 0f, quad.Normal));
            }

            indices.Add((ushort)(indexOffset + 0));
            indices.Add((ushort)(indexOffset + 1));
            indices.Add((ushort)(indexOffset + 2));
            indices.Add((ushort)(indexOffset + 0));
            indices.Add((ushort)(indexOffset + 2));
            indices.Add((ushort)(indexOffset + 3));
            indexOffset += 4;
        }
    }

    /// <summary>
    /// The axis a tree-wood hitbox's grain runs along: a limb's tube reaches exactly one cell
    /// boundary (its open end), so that axis; the node box, a whole trunk and a beveled shell
    /// (reaching none or several) grow along Y like the mesher's caps.
    /// </summary>
    public static int TubeAxis(BlockHitbox box)
    {
        var axis = 1;
        var touched = 0;
        for (var i = 0; i < 3; i++)
        {
            var lo = i == 0 ? box.Min.X : i == 1 ? box.Min.Y : box.Min.Z;
            var hi = i == 0 ? box.Max.X : i == 1 ? box.Max.Y : box.Max.Z;
            if (lo <= 0f) { touched++; axis = i; }
            if (hi >= 1f) { touched++; axis = i; }
        }

        return touched == 1 ? axis : 1;
    }

    private static ushort AxisTile(ushort[] faceTiles, int face, int capAxis)
    {
        if (ChunkMesher.FaceAxis[face] != capAxis) return faceTiles[0];
        return face is 1 or 3 or 4 ? faceTiles[4] : faceTiles[5];
    }

    /// <summary>
    /// Appends the six faces of <paramref name="grown"/> (cell-space grown corners) offset by
    /// <paramref name="blockPosition"/>, each face sampling <paramref name="crop"/>'s footprint
    /// of <paramref name="faceTiles"/>[face], or of the <paramref name="biomeTint"/> tiles when
    /// the block has them. Shaded fully unoccluded like the other standalone copies (drops,
    /// icons).
    /// </summary>
    public static void EmitBox(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        BlockHitbox grown, BlockHitbox crop, Vector3 blockPosition, ushort[] faceTiles,
        BiomeTintFaces? biomeTint = null, int capAxis = -1)
    {
        for (var face = 0; face < 6; face++)
        {
            ChunkFaceTables.BoxFaceCorners(grown.Min, grown.Max, face, out var a, out var b, out var c, out var d);
            ChunkFaceTables.BoxFaceCorners(crop.Min, crop.Max, face, out var ta, out var tb, out var tc, out var td);
            var shade = ChunkFaceTables.ShadeLut[(face << 2) | 3];
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
                Emit(vertices, indices, ref indexOffset, capAxis < 0 ? faceTiles[face] : AxisTile(faceTiles, face, capAxis), false);
            }

            void Emit(List<Vertex> vs, List<ushort> ids, ref ushort offset, ushort tile, bool tinted)
            {
                vs.Add(new Vertex(blockPosition + a, shade, shade, shade, 255, ChunkFaceTables.CropUv(face, ta), tile, 1f, 0f, normal, biomeTint: tinted));
                vs.Add(new Vertex(blockPosition + b, shade, shade, shade, 255, ChunkFaceTables.CropUv(face, tb), tile, 1f, 0f, normal, biomeTint: tinted));
                vs.Add(new Vertex(blockPosition + c, shade, shade, shade, 255, ChunkFaceTables.CropUv(face, tc), tile, 1f, 0f, normal, biomeTint: tinted));
                vs.Add(new Vertex(blockPosition + d, shade, shade, shade, 255, ChunkFaceTables.CropUv(face, td), tile, 1f, 0f, normal, biomeTint: tinted));
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
