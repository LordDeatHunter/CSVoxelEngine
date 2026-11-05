using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Data;
using VoxelEngine.Effects.Climate;
using VoxelEngine.Rendering.Geometry;
using static VoxelEngine.Chunks.Chunk;
using static VoxelEngine.Chunks.Meshing.ChunkLighting;

namespace VoxelEngine.Chunks.Meshing;

/// <summary>
/// GL-free chunk mesh emission shared by both mesh paths: the binary greedy pass, the
/// biome-tinted cube face and model-tint helpers, and the box-face and quad primitives the
/// per-voxel emitters (<see cref="FluidEmitter"/>, <see cref="StackEmitter"/>,
/// <see cref="TreeWoodEmitter"/>, <see cref="PaneEmitter"/>) build on.
/// </summary>
internal static class ChunkMesher
{
    // Axis each face's plane is perpendicular to (0 = X, 1 = Y, 2 = Z), in CubeFaces order,
    // and whether the face looks along +axis. Rows and columns of a face's bit plane are the two
    // other axes: X faces row = y, col = z; Y faces row = x, col = z; Z faces row = y, col = x.
    internal static readonly int[] FaceAxis = [2, 2, 0, 0, 1, 1];
    internal static readonly bool[] FacePositive = [false, true, false, true, true, false];

    // Bits 1..16 of an 18-bit padded column: the chunk's own cells (0 and 17 are the halo)
    private const uint ChunkBits = 0x1FFFE;

    /// <summary>
    /// Binary greedy pass over every <see cref="BlockType.IsGreedy"/> cell. Faces are found with
    /// bit columns (a cell shows a face when its greedy bit is set and the occluder bit one
    /// step along the face normal is not — the halo bits cull border faces exactly as the
    /// per-voxel neighbor read does), keyed by everything that would make two cells' quads
    /// differ (block, the four corner AO values, the four corner smooth sun/lamp light sums of
    /// <see cref="SmoothCornerLight"/> in a parallel 48-bit key, the submerged flag and, for a
    /// submerged face, its water surface code from <see cref="WaterSurfaceCodes.SubmergedSurfaceCode"/>),
    /// gathered per depth slice into one bit plane per key, and merged per plane into the widest,
    /// then tallest, rectangles. The output is pixel-identical
    /// to emitting each face on its own: a merge only happens along an axis where the key's AO
    /// and both lights are constant, so the big quad's interpolated shade and light equal the
    /// per-cell quads' (see
    /// <see cref="EmitGreedyRun"/>), and the UVs run 0..W / 0..H in tile units so the sampler's
    /// REPEAT tiles the texture per cell.
    /// </summary>
    internal static void EmitGreedyFaces(MeshScratch scratch, byte[] padded, byte[] paddedState,
        int chunkBaseY, byte[] sunLight, byte[] blockLight,
        List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        MeshBufferPool pool, ref List<Vertex>? tVertices, ref List<ushort>? tIndices,
        ref ushort tIndexOffset, in BiomeTintFields fields)
    {
        var greedyCols = scratch.GreedyColumns;
        var occCols = scratch.OccluderColumns;
        for (var a = 0; a < 3; a++)
        {
            Array.Clear(greedyCols[a]);
            Array.Clear(occCols[a]);
        }

        // A. One pass over the padded snapshot fills the three column sets. Column index is
        // (row + 1) * Padded + (col + 1) in the face's row/col convention; the bit is the padded
        // coordinate along the axis.
        var greedyById = BlockType.GreedyById;
        var occludesById = BlockType.OccludesById;
        var gX = greedyCols[0]; var gY = greedyCols[1]; var gZ = greedyCols[2];
        var oX = occCols[0]; var oY = occCols[1]; var oZ = occCols[2];
        var anyGreedy = false;
        for (var px = 0; px < Padded; px++)
        for (var py = 0; py < Padded; py++)
        {
            var rowBase = px * PaddedSq + py * Padded;
            for (var pz = 0; pz < Padded; pz++)
            {
                var id = padded[rowBase + pz];
                if (id == 0) continue;
                if (greedyById[id])
                {
                    gX[py * Padded + pz] |= 1u << px;
                    gY[px * Padded + pz] |= 1u << py;
                    gZ[py * Padded + px] |= 1u << pz;
                    anyGreedy = true;
                }
                if (occludesById[id])
                {
                    oX[py * Padded + pz] |= 1u << px;
                    oY[px * Padded + pz] |= 1u << py;
                    oZ[py * Padded + px] |= 1u << pz;
                }
            }
        }
        if (!anyGreedy) return;

        var visible = scratch.Visible;
        var table = scratch.PlaneTable;
        var planeKeys = scratch.PlaneKeys;
        var planeLightKeys = scratch.PlaneLightKeys;
        var planeTintKeys = scratch.PlaneTintKeys;
        var planeRows = scratch.PlaneRows;
        // The build's resolved 3x3 column terrain, for a submerged face's water surface
        var columns = scratch.Columns;

        for (var f = 0; f < 6; f++)
        {
            var axis = FaceAxis[f];
            var positive = FacePositive[f];
            var g = greedyCols[axis];
            var o = occCols[axis];
            var faceDelta = ChunkFaceTables.FaceNeighborDelta[f];
            var aoDeltas = ChunkFaceTables.AODeltas[f];

            // B. Face mask per column: a face shows where the cell is greedy and the cell one
            // step along the normal does not occlude
            var anyVisible = 0u;
            for (var row = 0; row < Size; row++)
            for (var col = 0; col < Size; col++)
            {
                var column = (row + 1) * Padded + (col + 1);
                var occ = o[column];
                var mask = g[column] & ~(positive ? occ >> 1 : occ << 1) & ChunkBits;
                visible[row * Size + col] = mask;
                anyVisible |= mask;
            }
            if (anyVisible == 0) continue;

            // C. Per depth slice: key every visible face and gather the faces of each key into
            // one 16x16 bit plane. Keys go through a small open-addressed table (at most 256
            // distinct keys per slice, 512 slots), which is cheaper than sorting the entries.
            for (var depth = 0; depth < Size; depth++)
            {
                var bit = 1u << (depth + 1);
                if ((anyVisible & bit) == 0) continue;
                var planeCount = 0;
                Array.Clear(table);

                for (var row = 0; row < Size; row++)
                for (var col = 0; col < Size; col++)
                {
                    if ((visible[row * Size + col] & bit) == 0) continue;

                    int x, y, z;
                    switch (axis)
                    {
                        case 0: x = depth; y = row; z = col; break;
                        case 1: x = row; y = depth; z = col; break;
                        default: x = col; y = row; z = depth; break;
                    }

                    var pIdx = (x + 1) * PaddedSq + (y + 1) * Padded + (z + 1);
                    var id = padded[pIdx];
                    var neighbor = padded[pIdx + faceDelta];
                    // Glass panes fuse: a face against the same block is hidden (the occluder
                    // mask already handled opaque neighbors)
                    if (neighbor == id && BlockType.ById[id]!.CullSelf) continue;

                    var lightBase = LightIdx(x, y, z);
                    var ao = 0;
                    var light = 0UL;
                    var tint = 0UL;
                    for (var j = 0; j < 4; j++)
                    {
                        var side1Id = padded[pIdx + aoDeltas[j * 3 + 0]];
                        var side2Id = padded[pIdx + aoDeltas[j * 3 + 1]];
                        var cornerId = padded[pIdx + aoDeltas[j * 3 + 2]];
                        ao |= ChunkFaceTables.VertexAO(BlockType.OccludesAO(side1Id), BlockType.OccludesAO(side2Id),
                            BlockType.OccludesAO(cornerId)) << (2 * j);
                        var code = SmoothCornerLight(f, j, sunLight, blockLight, lightBase,
                            side1Id, side2Id, cornerId);
                        light |= (ulong)(code & 0xFFF) << (12 * j);
                        tint |= (ulong)(code >> 12) << (10 * j);
                    }

                    // Key: id 0-7, AO 8-15, 16-23 unused, submerged 24, and for a submerged
                    // face its 7-bit water surface code in 25-31. Bit 31 can make the key
                    // negative; that is safe because the slot hash reads it as (uint)key and an
                    // empty slot is table entry 0, never a key value. The light travels in a
                    // parallel 48-bit key: four 12-bit SmoothCornerLight codes, corner j at 12j,
                    // and the color in a second parallel 40-bit key: the four 10-bit LightTint
                    // codes, corner j at 10j (all zero wherever the lamp light is default-colored).
                    var key = id | ao << 8;
                    if (BlockType.IsFluid(neighbor))
                        key |= 1 << 24 | WaterSurfaceCodes.SubmergedSurfaceCode(padded, paddedState, pIdx + faceDelta,
                            chunkBaseY, columns) << 25;

                    // Find or open the key's plane
                    var h = ((ulong)(uint)key ^ light * 0x9E3779B97F4A7C15UL ^ tint * 0xC2B2AE3D27D4EB4FUL)
                            * 0xBF58476D1CE4E5B9UL;
                    var slot = (int)(h >> (64 - MeshScratch.PlaneTableBits));
                    int plane;
                    while (true)
                    {
                        var entry = table[slot];
                        if (entry == 0)
                        {
                            plane = planeCount++;
                            table[slot] = plane + 1;
                            planeKeys[plane] = key;
                            planeLightKeys[plane] = light;
                            planeTintKeys[plane] = tint;
                            Array.Clear(planeRows, plane * Size, Size);
                            break;
                        }
                        if (planeKeys[entry - 1] == key && planeLightKeys[entry - 1] == light
                            && planeTintKeys[entry - 1] == tint)
                        {
                            plane = entry - 1;
                            break;
                        }
                        slot = (slot + 1) & (MeshScratch.PlaneTableSize - 1);
                    }
                    planeRows[plane * Size + row] |= (ushort)(1 << col);
                }

                // D + E. Merge and emit each key's plane
                for (var plane = 0; plane < planeCount; plane++)
                {
                    var key = planeKeys[plane];
                    var light = planeLightKeys[plane];
                    var tint = planeTintKeys[plane];
                    var type = BlockType.ById[key & 0xFF]!;
                    var rows = planeRows.AsSpan(plane * Size, Size);
                    if (type.Translucent)
                    {
                        tVertices ??= pool.RentTranslucentVertices();
                        tIndices ??= pool.RentTranslucentIndices();
                        EmitGreedyRun(f, axis, depth, chunkBaseY, key, light, tint, type, rows, tVertices, tIndices, ref tIndexOffset, in fields);
                    }
                    else
                        EmitGreedyRun(f, axis, depth, chunkBaseY, key, light, tint, type, rows, vertices, indices, ref indexOffset, in fields);
                }
            }
        }
    }

    /// <summary>
    /// Merges one key's bit plane into rectangles and emits them. Every cell in the plane has the
    /// same four corner AO values and the same four corner smooth sun/lamp light sums
    /// (<paramref name="light"/>), but that alone does not make them continuous across two cells:
    /// for ordinary occluders the shared-vertex geometry makes AO so, while a neighbor that
    /// casts AO without occluding (leaves) can leave a step at a shared edge, and
    /// light has a gradient wherever a lamp or a shadow edge is near. So a run only grows along an
    /// axis where the key's per-corner code (AO and both lights together) is the same at both
    /// ends of that axis — then the two cells' shared corners carry the same values, shade and
    /// light inside every cell are affine along it, and the merged quad interpolates to exactly
    /// the per-cell result. Uniformly lit terrain merges both ways; a strip beside a wall merges
    /// along the wall. The color tint code (<paramref name="tint"/>) joins the per-corner code
    /// too: the shader decodes each corner's code to an RGB tint and lerps that linearly across the
    /// quad, so equal corner codes along a merged axis give the identical color gradient.
    /// </summary>
    internal static void EmitGreedyRun(int f, int axis, int depth, int chunkBaseY, int key, ulong light, ulong tint,
        BlockType type,
        Span<ushort> rows, List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset, in BiomeTintFields fields)
    {
        var ao = (key >> 8) & 0xFF;
        // Per-corner code: AO in bits 0-1, the 12-bit smooth light code in 2-13, the 10-bit
        // tint code in 14-23
        Span<int> code = stackalloc int[4];
        for (var j = 0; j < 4; j++)
            code[j] = ((ao >> (2 * j)) & 3) | (int)((light >> (12 * j)) & 0xFFF) << 2
                      | (int)((tint >> (10 * j)) & 0x3FF) << 14;
        // Corner pairs that differ along the col axis: (c0,c1),(c3,c2) on Y faces (c0-c1 runs
        // along z there), (c0,c3),(c1,c2) on X and Z faces; the row axis takes the other pair.
        var yFace = axis == 1;
        var alongCol = yFace ? code[0] == code[1] && code[3] == code[2] : code[0] == code[3] && code[1] == code[2];
        var alongRow = yFace ? code[0] == code[3] && code[1] == code[2] : code[0] == code[1] && code[3] == code[2];

        var submerged = ((key >> 24) & 1) != 0;
        var waterSurface = submerged
            ? Vertex.EncodeWaterSurface(WaterSurfaceCodes.DecodeSurfaceCode(chunkBaseY, (key >> 25) & 127))
            : (ushort)0;
        var layer = type.FaceTiles[f];
        var sway = type.Sway ? 1f : 0f;
        var normal = CubeFaces[f].normal;
        var corners = CubeFaces[f].face;

        // A biome-tinted block's corner values, decoded once per plane exactly as the plain
        // vertex loop below decodes them per quad (the key holds them constant over the plane)
        var biomeTint = type.BiomeTint;
        Span<byte> tShade = stackalloc byte[4];
        Span<float> tSun = stackalloc float[4];
        Span<float> tBlock = stackalloc float[4];
        Span<ushort> tTint = stackalloc ushort[4];
        if (biomeTint != null)
        {
            for (var j = 0; j < 4; j++)
            {
                tShade[j] = ChunkFaceTables.ShadeLut[(f << 2) | ((ao >> (2 * j)) & 3)];
                tSun[j] = (int)((light >> (12 * j)) & 63) / ChunkFaceTables.SmoothLightDivisor;
                tBlock[j] = (int)((light >> (12 * j + 6)) & 63) / ChunkFaceTables.SmoothLightDivisor;
                tTint[j] = (ushort)((tint >> (10 * j)) & 0x3FF);
            }
        }

        for (var row = 0; row < Size; row++)
        {
            var bits = (int)rows[row];
            while (bits != 0)
            {
                var start = BitOperations.TrailingZeroCount(bits);
                // Run of set bits from start; ~(bits >> start) has a zero above bit 15 at the
                // latest, so width never passes the plane's edge
                var width = alongCol ? BitOperations.TrailingZeroCount(~(bits >> start)) : 1;
                var mask = ((1 << width) - 1) << start;
                var height = 1;
                if (alongRow)
                {
                    while (row + height < Size && (rows[row + height] & mask) == mask)
                    {
                        rows[row + height] &= (ushort)~mask;
                        height++;
                    }
                }
                bits &= ~mask;

                // Meshes are indexed with ushort (mirrors EmitFluid): stop emitting into a full
                // list instead of wrapping the offset
                if (vertices.Count > ushort.MaxValue - 3) return;

                Vector3 origin, extent;
                switch (axis)
                {
                    case 0:
                        origin = new Vector3(depth, row, start);
                        extent = new Vector3(1f, height, width);
                        break;
                    case 1:
                        origin = new Vector3(row, depth, start);
                        extent = new Vector3(height, 1f, width);
                        break;
                    default:
                        origin = new Vector3(start, row, depth);
                        extent = new Vector3(width, height, 1f);
                        break;
                }

                // A biome-tinted block (grass, leaves) goes through the emitter the per-voxel
                // path shares, so both paths give identical output. The tint is a function of
                // vertex position, not of the cell, so it never splits a merge.
                if (biomeTint is { } bt)
                {
                    EmitTintedCubeFace(vertices, indices, ref indexOffset, f, origin, extent,
                        tShade, tSun, tBlock, tTint, bt, fields.Field(bt.Palette), submerged, waterSurface, sway);
                    continue;
                }

                // Corner j of the merged quad sits where corner j of a cell quad would, scaled
                // by the extent, so key AO and light corner j land on the matching corner
                for (var j = 0; j < 4; j++)
                {
                    var local = corners[j] * extent;
                    var shade = ChunkFaceTables.ShadeLut[(f << 2) | ((ao >> (2 * j)) & 3)];
                    var sun = (int)((light >> (12 * j)) & 63) / ChunkFaceTables.SmoothLightDivisor;
                    var block = (int)((light >> (12 * j + 6)) & 63) / ChunkFaceTables.SmoothLightDivisor;
                    vertices.Add(new Vertex(origin + local, shade, shade, shade, 255,
                        ChunkFaceTables.TiledUv(f, local, extent), layer, sun, block, normal, sway, submerged, waterSurface,
                        (ushort)((tint >> (10 * j)) & 0x3FF)));
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
    }

    /// <summary>
    /// Emits one face of a biome-tinted cube (<see cref="BlockType.BiomeTint"/>), merged over
    /// <paramref name="extent"/> or a single cell, for both the greedy pass and the per-voxel
    /// replay, so their output is identical by construction. Up to two coplanar quads: first the
    /// untinted base (grass sides: dirt) when the face keeps one, then the tinted quad, whose rgb
    /// is shade * the field's tint at each corner and whose packed normal sets bit 4 so the world
    /// fragment shader applies <see cref="BiomeTint.Gain"/>. The base goes first so the overlay,
    /// at identical depth, wins under Lequal. Corner values are in <see cref="CubeFaces"/> order;
    /// the field is sampled at the chunk-local vertex position, so neighboring chunks agree.
    /// </summary>
    internal static void EmitTintedCubeFace(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        int f, Vector3 origin, Vector3 extent, ReadOnlySpan<byte> shade, ReadOnlySpan<float> sun,
        ReadOnlySpan<float> block, ReadOnlySpan<ushort> lightTint, BiomeTintFaces bt, in BiomeTintField field,
        bool submerged, ushort waterSurface, float sway)
    {
        var normal = CubeFaces[f].normal;
        var corners = CubeFaces[f].face;

        if (bt.Base[f] != BiomeTintFaces.NoTile)
        {
            if (vertices.Count > ushort.MaxValue - 3) return;
            var layer = bt.Base[f];
            for (var j = 0; j < 4; j++)
            {
                var local = corners[j] * extent;
                vertices.Add(new Vertex(origin + local, shade[j], shade[j], shade[j], 255,
                    ChunkFaceTables.TiledUv(f, local, extent), layer, sun[j], block[j], normal, sway, submerged, waterSurface,
                    lightTint[j]));
            }
            AddQuadIndices(indices, ref indexOffset);
        }

        if (bt.Tinted[f] != BiomeTintFaces.NoTile)
        {
            if (vertices.Count > ushort.MaxValue - 3) return;
            var layer = bt.Tinted[f];
            for (var j = 0; j < 4; j++)
            {
                var local = corners[j] * extent;
                var position = origin + local;
                BiomeTint.Shade(shade[j], field.Sample(position.X, position.Z), out var r, out var g, out var b);
                vertices.Add(new Vertex(position, r, g, b, 255,
                    ChunkFaceTables.TiledUv(f, local, extent), layer, sun[j], block[j], normal, sway, submerged, waterSurface,
                    lightTint[j], biomeTint: true));
            }
            AddQuadIndices(indices, ref indexOffset);
        }

        static void AddQuadIndices(List<ushort> indices, ref ushort indexOffset)
        {
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
    /// Recolors one quad vertex of a <c>modelTint</c> block: its rgb becomes the vertex's shade
    /// times the palette's field at the vertex, flagged so the world fragment shader applies
    /// <see cref="BiomeTint.Gain"/>. The vertex's rgb standing in for its shade (three equal
    /// channels, 255 on an unshaded quad) is what makes the shade recoverable here.
    /// </summary>
    internal static Vertex ApplyModelTint(Vertex vertex, in BiomeTintField field)
    {
        var shade = (byte)(vertex.PackedColor & 0xFF);
        BiomeTint.Shade(shade, field.Sample(vertex.Position.X, vertex.Position.Z), out var r, out var g, out var b);
        return vertex.WithBiomeTint(r, g, b);
    }

    /// <summary>
    /// The alpha byte of a model-path vertex at cell-space height <paramref name="cellY"/>: a
    /// rooted plant's sway weight (<see cref="BlockType.SwayWeightAt"/>, so its roots stay put
    /// and its tip moves fully), 255 for every other block - full weight on canopy leaves, and
    /// the plain opaque alpha on a block that does not sway at all.
    /// </summary>
    internal static byte ModelVertexAlpha(BlockType type, float cellY) =>
        type.SwaysFromRoot ? Vertex.EncodeSwayWeight(type.SwayWeightAt(cellY)) : (byte)255;

    /// <summary>One axis-aligned face of the [min, max] box (cell-local), with flat shading
    /// and cropped UVs (see <see cref="ChunkFaceTables.CropUv"/>).</summary>
    internal static void EmitBoxFace(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        Vector3 basePos, Vector3 min, Vector3 max, int face,
        ushort tile, byte shade, in FaceLight light)
    {
        ChunkFaceTables.BoxFaceCorners(min, max, face, out var a, out var b, out var c, out var d);
        EmitQuad(vertices, indices, ref indexOffset, basePos, a, b, c, d, CubeFaces[face].normal,
            ChunkFaceTables.CropUv(face, a), ChunkFaceTables.CropUv(face, b), ChunkFaceTables.CropUv(face, c), ChunkFaceTables.CropUv(face, d), tile,
            shade, light);
    }

    /// <summary>One arbitrary quad: four cell-local corners, one normal, one tile-local UV per
    /// corner, all on atlas slice <paramref name="tile"/>; each corner's light is sampled from
    /// <paramref name="light"/> at its cell-local position.</summary>
    internal static void EmitQuad(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        Vector3 basePos, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal,
        Vector2 uva, Vector2 uvb, Vector2 uvc, Vector2 uvd, ushort tile,
        byte shade, in FaceLight light, byte alpha = 255)
    {
        light.Sample(a, out var sa, out var ba, out var ta);
        light.Sample(b, out var sb, out var bb, out var tb);
        light.Sample(c, out var sc, out var bc, out var tc);
        light.Sample(d, out var sd, out var bd, out var td);
        vertices.Add(new Vertex(basePos + a, shade, shade, shade, alpha, uva, tile, sa, ba, normal, tint: ta));
        vertices.Add(new Vertex(basePos + b, shade, shade, shade, alpha, uvb, tile, sb, bb, normal, tint: tb));
        vertices.Add(new Vertex(basePos + c, shade, shade, shade, alpha, uvc, tile, sc, bc, normal, tint: tc));
        vertices.Add(new Vertex(basePos + d, shade, shade, shade, alpha, uvd, tile, sd, bd, normal, tint: td));

        indices.Add((ushort)(indexOffset + 0));
        indices.Add((ushort)(indexOffset + 1));
        indices.Add((ushort)(indexOffset + 2));
        indices.Add((ushort)(indexOffset + 0));
        indices.Add((ushort)(indexOffset + 2));
        indices.Add((ushort)(indexOffset + 3));
        indexOffset += 4;
    }
}
