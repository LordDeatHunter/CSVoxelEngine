using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Data;
using VoxelEngine.Effects.Climate;
using VoxelEngine.Effects.Lighting;
using VoxelEngine.Simulation;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using VoxelEngine.WorldGen.Trees;
using static VoxelEngine.Chunks.Chunk;
using static VoxelEngine.Chunks.ChunkLighting;

namespace VoxelEngine.Chunks;

/// <summary>
/// GL-free chunk mesh emission extracted from <see cref="Chunk"/>: the binary greedy pass
/// and the per-voxel procedural emitters, plus the water-surface codes they key on.
/// </summary>
internal static class ChunkMesher
{
    // Two-regime 7-bit code of a submerged face's water surface (SubmergedSurfaceCode):
    // 1..68 quarter blocks inside the padded snapshot, 69..127 whole blocks above its top.
    private const int SurfaceCodeInsideMax = 68;
    private const int SurfaceCodeAboveBase = 69;
    private const int SurfaceCodeMax = 127;

    /// <summary>
    /// The water surface above a submerged face, as the 7-bit code that joins the greedy key
    /// (bits 25-31), so a merge never crosses two different surfaces. It is the same surface
    /// <see cref="ChunkManager.GetFluidColumnTopGlobal"/> finds, resolved from the padded snapshot
    /// alone: the fluid column is walked up from <paramref name="fluidPIdx"/> (the fluid cell the
    /// face looks into). A column that tops out inside the snapshot codes its surface in quarter
    /// blocks (1..68). One that leaves the snapshot's top row still in fluid reads the column's
    /// <c>WaterLevel</c> from the build's resolved <see cref="ColumnTerrain"/>
    /// (<paramref name="columns"/>, the 3x3 in <c>dx * 3 + dz</c> order, worldgen or streamed) and
    /// codes whole blocks above the snapshot top (69..127, clamped ~58 blocks above it, where the
    /// caustic fade is already ~0.01); a column whose level is lower than that (a player-built
    /// tower) caps at the snapshot top. <see cref="DecodeSurfaceCode"/> turns the code back into
    /// a world Y.
    /// </summary>
    internal static int SubmergedSurfaceCode(byte[] padded, byte[] paddedState, int fluidPIdx,
        int chunkBaseY, ColumnTerrain[] columns)
    {
        // FillPaddedSnapshot's layout: px * PaddedSq + py * Padded + pz (z innermost)
        var px = fluidPIdx / PaddedSq;
        var py = fluidPIdx % PaddedSq / Padded;
        var pz = fluidPIdx % Padded;

        var top = fluidPIdx;
        while (py < Padded - 1 && BlockType.IsFluid(padded[top + Padded]))
        {
            top += Padded;
            py++;
        }

        if (py < Padded - 1)
        {
            // The column's top cell lies inside the snapshot: its surface in chunk-local Y
            var ls = (py - 1) + FluidState.SurfaceHeight(paddedState[top]);
            return Math.Clamp((int)MathF.Round((ls + 1f) * 4f), 1, SurfaceCodeInsideMax);
        }

        // Still fluid in the snapshot's top row: the surface lies above what this build sees.
        // Arithmetic shift: padded 0 is the -X/-Z neighbor column (-1 >> 4 = -1), 1..16 this
        // one, 17 the +X/+Z one; the mask then picks the cell inside that column.
        var dx = ((px - 1) >> SizeShift) + 1;
        var dz = ((pz - 1) >> SizeShift) + 1;
        var level = columns[dx * 3 + dz].WaterLevel[(((px - 1) & SizeMask) << SizeShift) | ((pz - 1) & SizeMask)];
        var cap = chunkBaseY + Padded - 1;
        var surface = level != TerrainGenerator.NoWater && level + FluidState.SourceHeight > cap
            ? level + FluidState.SourceHeight
            : cap;
        return SurfaceCodeAboveBase
               + Math.Clamp((int)MathF.Round(surface - cap), 0, SurfaceCodeMax - SurfaceCodeAboveBase);
    }

    /// <summary>World Y of the water surface a <see cref="SubmergedSurfaceCode"/> code stands
    /// for, in a chunk whose lowest block row is <paramref name="chunkBaseY"/>.</summary>
    internal static float DecodeSurfaceCode(int chunkBaseY, int code) =>
        code <= SurfaceCodeInsideMax
            ? chunkBaseY + code / 4f - 1f
            : chunkBaseY + Padded - 1 + (code - SurfaceCodeAboveBase);

    /// <summary>The baked <see cref="Vertex.WaterSurface"/> of a face looking into the fluid
    /// cell at <paramref name="fluidPIdx"/>; the per-voxel twin of the greedy key's code.</summary>
    internal static ushort BakedWaterSurface(byte[] padded, byte[] paddedState, int fluidPIdx,
        int chunkBaseY, ColumnTerrain[] columns) =>
        Vertex.EncodeWaterSurface(DecodeSurfaceCode(chunkBaseY,
            SubmergedSurfaceCode(padded, paddedState, fluidPIdx, chunkBaseY, columns)));

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
    /// submerged face, its water surface code from <see cref="SubmergedSurfaceCode"/>),
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
        List<Vertex> tVertices, List<ushort> tIndices, ref ushort tIndexOffset, in BiomeTintFields fields)
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
            var faceDelta = FaceNeighborDelta[f];
            var aoDeltas = AODeltas[f];

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
                        ao |= VertexAO(BlockType.OccludesAO(side1Id), BlockType.OccludesAO(side2Id),
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
                        key |= 1 << 24 | SubmergedSurfaceCode(padded, paddedState, pIdx + faceDelta,
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
                        EmitGreedyRun(f, axis, depth, chunkBaseY, key, light, tint, type, rows, tVertices, tIndices, ref tIndexOffset, in fields);
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
            ? Vertex.EncodeWaterSurface(DecodeSurfaceCode(chunkBaseY, (key >> 25) & 127))
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
                tShade[j] = ShadeLut[(f << 2) | ((ao >> (2 * j)) & 3)];
                tSun[j] = (int)((light >> (12 * j)) & 63) / SmoothLightDivisor;
                tBlock[j] = (int)((light >> (12 * j + 6)) & 63) / SmoothLightDivisor;
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
                    var shade = ShadeLut[(f << 2) | ((ao >> (2 * j)) & 3)];
                    var sun = (int)((light >> (12 * j)) & 63) / SmoothLightDivisor;
                    var block = (int)((light >> (12 * j + 6)) & 63) / SmoothLightDivisor;
                    vertices.Add(new Vertex(origin + local, shade, shade, shade, 255,
                        TiledUv(f, local, extent), layer, sun, block, normal, sway, submerged, waterSurface,
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
                    TiledUv(f, local, extent), layer, sun[j], block[j], normal, sway, submerged, waterSurface,
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
                    TiledUv(f, local, extent), layer, sun[j], block[j], normal, sway, submerged, waterSurface,
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

    /// <summary>
    /// Emits one fluid cell's surface into the translucent lists. The top face is a quad whose
    /// four corners sit at <see cref="CornerHeight"/>, so a flow slopes away from its source
    /// and - the average being symmetric between neighbors - adjacent tops meet exactly with
    /// no crack. No face is ever emitted between two fluid cells, so a body of water reads as
    /// a single skin instead of a stack of blended cubes.
    /// </summary>
    internal static void EmitFluid(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        byte[] padded, byte[] paddedState, int pIdx, byte[] sunLight, byte[] blockLight,
        int x, int y, int z, BlockType type, Vector3 basePos)
    {
        // Smooth light per (X, Z) corner, averaged over the same 2x2 cells CornerHeight reads.
        // That set is identical for every fluid cell meeting at the corner and the fill of a
        // blocked sample is order-independent, so neighbors agree at shared corners exactly as
        // their heights do and a shoreline shows no light step along a cell edge. (Full cube
        // faces use SmoothCornerLight's asymmetric front-substitution rule instead.) Side and
        // bottom vertices sit on integer X/Z, so they take the corner values unchanged.
        var lightBase = LightIdx(x, y, z);
        var l00 = FluidCornerLight(padded, pIdx, sunLight, blockLight, lightBase, 0, 0);
        var l01 = FluidCornerLight(padded, pIdx, sunLight, blockLight, lightBase, 0, 1);
        var l10 = FluidCornerLight(padded, pIdx, sunLight, blockLight, lightBase, 1, 0);
        var l11 = FluidCornerLight(padded, pIdx, sunLight, blockLight, lightBase, 1, 1);
        var topCorners = CubeFaces[4].face;
        var light = FaceLight.FromCorners(4,
            FluidCornerPick(topCorners[0], l00, l01, l10, l11),
            FluidCornerPick(topCorners[1], l00, l01, l10, l11),
            FluidCornerPick(topCorners[2], l00, l01, l10, l11),
            FluidCornerPick(topCorners[3], l00, l01, l10, l11));

        // Corner heights indexed by the corner's own (X, Z) inside the cell: cornerX * 2 + cornerZ
        Span<float> corners = stackalloc float[4];
        var minCorner = float.MaxValue;
        for (var cx = 0; cx < 2; cx++)
        for (var cz = 0; cz < 2; cz++)
        {
            var h = CornerHeight(padded, paddedState, pIdx,
                cx == 0 ? -PaddedSq : PaddedSq, cz == 0 ? -1 : 1);
            corners[cx * 2 + cz] = h;
            if (h < minCorner) minCorner = h;
        }

        // A fluid's model only supplies its tile (see the block schema), so every face uses it
        var tile = type.Top;

        // Per-corner flow codes (Vertex.EncodeWaterFlow), indexed like `corners`. Computed on the
        // first face that survives the culls, so a fully enclosed sea cell pays nothing.
        Span<ushort> flowCodes = stackalloc ushort[4];
        var flowComputed = false;
        var anyFlow = false;

        for (var face = 0; face < 6; face++)
        {
            var neighbor = padded[pIdx + FaceNeighborDelta[face]];

            // Two fluid cells share no face: their sloped tops already meet at the corners
            if (BlockType.IsFluid(neighbor)) continue;
            if (face != 4 && BlockType.Occludes(neighbor)) continue;
            // A full column pressed against a ceiling has a coplanar top face - drop it
            // instead of z-fighting the block above
            if (face == 4 && BlockType.Occludes(neighbor) && minCorner >= 0.999f) continue;

            // Meshes are indexed with ushort (mirrors LodRegion.AddQuad): stop emitting
            // instead of wrapping the offset if a chunk somehow runs past the limit
            if (vertices.Count > ushort.MaxValue - 3) return;

            if (!flowComputed)
            {
                flowComputed = true;
                anyFlow = ComputeFluidFlowCodes(padded, paddedState, pIdx, flowCodes);
            }

            var faceCorners = CubeFaces[face].face;
            var a = LowerCorner(faceCorners[0], corners);
            var b = LowerCorner(faceCorners[1], corners);
            var c = LowerCorner(faceCorners[2], corners);
            var d = LowerCorner(faceCorners[3], corners);

            // A sloped top needs the normal of the quad it actually became, or the sun
            // diffuse ignores the slope; the other five faces stay axis-aligned
            var normal = face == 4
                ? Vector3.Normalize(Vector3.Cross(c - a, d - b))
                : CubeFaces[face].normal;

            // ponytail: alpha 254 flags water for the shader (glass stays 255); fColor.a is unused otherwise.
            EmitQuad(vertices, indices, ref indexOffset, basePos, a, b, c, d, normal,
                CropUv(face, a), CropUv(face, b), CropUv(face, c), CropUv(face, d), tile,
                ShadeLut[(face << 2) | 3], light, 254);

            // EmitQuad appended a, b, c, d in that order; each vertex takes its (X, Z) corner's
            // flow code (LowerCorner keeps X/Z at 0 or 1, so side and bottom vertices do too)
            if (anyFlow)
            {
                var span = CollectionsMarshal.AsSpan(vertices);
                span[^4].WaterSurface = flowCodes[(int)a.X * 2 + (int)a.Z];
                span[^3].WaterSurface = flowCodes[(int)b.X * 2 + (int)b.Z];
                span[^2].WaterSurface = flowCodes[(int)c.X * 2 + (int)c.Z];
                span[^1].WaterSurface = flowCodes[(int)d.X * 2 + (int)d.Z];
            }
        }
    }

    /// <summary>
    /// Fills the four per-corner flow codes of the fluid cell at <paramref name="pIdx"/>
    /// (<see cref="FluidFlow.Corner{T}"/> scaled by <see cref="WorldGenerationConfig.WaterFlowFullStrength"/>,
    /// plus the cell's falling flag) and reports whether any is nonzero. Falling is the cell's own
    /// state alone, never "fluid above": a stack of still sources is not a waterfall.
    /// </summary>
    internal static bool ComputeFluidFlowCodes(byte[] padded, byte[] paddedState, int pIdx, Span<ushort> codes)
    {
        var cells = new PaddedFluidCells(padded, paddedState, pIdx);
        var falling = FluidState.IsFalling(paddedState[pIdx]);
        var any = false;
        for (var cx = 0; cx < 2; cx++)
        for (var cz = 0; cz < 2; cz++)
        {
            var v = FluidFlow.Corner(cells, 0, 0, 0, cx == 0 ? -1 : 1, cz == 0 ? -1 : 1);
            var dir = v.NormalizeSafe();
            var strength = dir == Vector3.Zero
                ? 0f
                : MathF.Min(v.Length() / WorldGenerationConfig.WaterFlowFullStrength, 1f);
            var code = Vertex.EncodeWaterFlow(new Vector2(dir.X, dir.Z) * strength, falling);
            codes[cx * 2 + cz] = code;
            any |= code != 0;
        }

        return any;
    }

    /// <summary>
    /// <see cref="IFluidCellReader"/> over the mesher's padded snapshot, in coordinates relative
    /// to the cell at <paramref name="pIdx"/>. Always succeeds: <see cref="FluidFlow.Corner{T}"/>
    /// reads only within ±1 horizontally at y and y - 1, which stays inside the 18³ snapshot for
    /// any own cell at padded 1..16, and an absent edge/corner chunk reads as air there - the same
    /// "no term" branch a live TryGetCell == false would take.
    /// </summary>
    private readonly struct PaddedFluidCells(byte[] padded, byte[] paddedState, int pIdx) : IFluidCellReader
    {
        public bool TryGetCell(int x, int y, int z, out byte id, out byte state)
        {
            var q = pIdx + x * PaddedSq + y * Padded + z;
            id = padded[q];
            state = paddedState[q];
            return true;
        }
    }

    internal static int FluidCornerPick(Vector3 corner, int l00, int l01, int l10, int l11) =>
        corner.X == 0f ? (corner.Z == 0f ? l00 : l01) : (corner.Z == 0f ? l10 : l11);

    /// <summary>
    /// Smooth light code (<c>sunSum | blockSum &lt;&lt; 6 | tint &lt;&lt; 12</c>, the tint a
    /// <see cref="LightTint"/> code over the same four samples) of one top corner of a fluid cell,
    /// summed over the own, dx, dz and diagonal cells at the fluid's height (the cells
    /// <see cref="CornerHeight"/> reads). A cell that does not pass light stores 0, so it is
    /// replaced by the brightest open sample, per channel; the own fluid cell always passes. The
    /// rule never asks which of the four cells is the own one, so every fluid cell sharing the
    /// corner computes the same code.
    /// </summary>
    internal static int FluidCornerLight(byte[] padded, int pIdx, byte[] sunLight, byte[] blockLight,
        int lightBase, int cx, int cz)
    {
        var pdx = cx == 0 ? -PaddedSq : PaddedSq;
        var ldx = cx == 0 ? -LightSq : LightSq;
        var dz = cz == 0 ? -1 : 1;
        var passes = BlockType.PassesLightById;
        var o1 = passes[padded[pIdx + pdx]];
        var o2 = passes[padded[pIdx + dz]];
        var o3 = passes[padded[pIdx + pdx + dz]];

        int s0 = sunLight[lightBase], s1 = sunLight[lightBase + ldx],
            s2 = sunLight[lightBase + dz], s3 = sunLight[lightBase + ldx + dz];
        // blockLight holds level | slot << 4 (see ComputeLight)
        int r0 = blockLight[lightBase], r1 = blockLight[lightBase + ldx],
            r2 = blockLight[lightBase + dz], r3 = blockLight[lightBase + ldx + dz];
        int b0 = r0 & BlockLevelMask, b1 = r1 & BlockLevelMask,
            b2 = r2 & BlockLevelMask, b3 = r3 & BlockLevelMask;

        var sMax = s0;
        var bMax = b0;
        if (o1) { sMax = Math.Max(sMax, s1); bMax = Math.Max(bMax, b1); }
        if (o2) { sMax = Math.Max(sMax, s2); bMax = Math.Max(bMax, b2); }
        if (o3) { sMax = Math.Max(sMax, s3); bMax = Math.Max(bMax, b3); }

        var sun = s0 + (o1 ? s1 : sMax) + (o2 ? s2 : sMax) + (o3 ? s3 : sMax);
        var block = b0 + (o1 ? b1 : bMax) + (o2 ? b2 : bMax) + (o3 ? b3 : bMax);

        // A blocked sample takes the brightest open sample's color too; on a level tie the
        // lowest slot among the open samples at that level, so the fill never depends on which
        // of the four cells is the own one and neighboring fluid cells agree at the corner
        int sl0 = (r0 >> 4) & 7, sl1 = (r1 >> 4) & 7, sl2 = (r2 >> 4) & 7, sl3 = (r3 >> 4) & 7;
        var slMax = 7;
        if (b0 == bMax) slMax = sl0;
        if (o1 && b1 == bMax && sl1 < slMax) slMax = sl1;
        if (o2 && b2 == bMax && sl2 < slMax) slMax = sl2;
        if (o3 && b3 == bMax && sl3 < slMax) slMax = sl3;
        var tint = LightTint.FromSamples(b0, sl0,
            o1 ? b1 : bMax, o1 ? sl1 : slMax,
            o2 ? b2 : bMax, o2 ? sl2 : slMax,
            o3 ? b3 : bMax, o3 ? sl3 : slMax);
        return sun | block << 6 | tint << 12;
    }

    /// <summary>Drops a cube corner's top vertex to the fluid surface, so side faces crop to
    /// the real height and the top face slopes. Bottom corners pass through unchanged.</summary>
    internal static Vector3 LowerCorner(Vector3 corner, Span<float> cornerHeights) =>
        corner.Y == 0f
            ? corner
            : new Vector3(corner.X, cornerHeights[(int)corner.X * 2 + (int)corner.Z], corner.Z);

    /// <summary>Surface height inside one fluid cell of the padded snapshot, 0..1. Fluid
    /// directly above fills the cell completely, so stacked water shows no seam.</summary>
    internal static float FluidHeight(byte[] padded, byte[] paddedState, int p) =>
        BlockType.IsFluid(padded[p + FaceNeighborDelta[4]])
            ? 1f
            : FluidState.SurfaceHeight(paddedState[p]);

    /// <summary>
    /// Height of the fluid surface at one corner of a cell, averaged over the cells that meet
    /// there (<paramref name="dx"/> and <paramref name="dz"/> are the padded index deltas
    /// toward the corner). Nearly full cells outweigh thin ones ten to one, so a source next
    /// to a skirt keeps its own level; air and cutouts pull the corner down toward 0; an
    /// occluding solid is skipped, because a wall must not lower the water beside it. The
    /// diagonal cell counts only when at least one of the two orthogonal neighbors is fluid
    /// (Minecraft's rule), so two bodies of water touching across a corner with solids in both
    /// orthogonal cells stay separate instead of visually joining. The cell itself is fluid, so
    /// the weight is never zero.
    /// </summary>
    internal static float CornerHeight(byte[] padded, byte[] paddedState, int p, int dx, int dz)
    {
        var total = 0f;
        var weight = 0f;
        // The diagonal is only reachable through a fluid orthogonal neighbor; with neither of
        // them fluid it is ignored outright - no height, no full-column pin, no weight
        var orthogonalFluid = false;

        for (var i = 0; i < 4; i++)
        {
            // i: 0 = own cell, 1 = the dx neighbor, 2 = the dz neighbor, 3 = the diagonal
            if (i == 3 && !orthogonalFluid) break;

            var q = p + ((i & 1) != 0 ? dx : 0) + ((i & 2) != 0 ? dz : 0);
            var id = padded[q];

            if (BlockType.IsFluid(id))
            {
                if (i is 1 or 2) orthogonalFluid = true;

                // A full column touching this corner pins it to the cell top
                if (BlockType.IsFluid(padded[q + FaceNeighborDelta[4]])) return 1f;

                var h = FluidHeight(padded, paddedState, q);
                if (h >= 0.8f)
                {
                    total += h * 10f;
                    weight += 10f;
                }
                else
                {
                    total += h;
                    weight += 1f;
                }
            }
            else if (!BlockType.Occludes(id))
            {
                weight += 1f;
            }
        }

        return total / weight;
    }

    /// <summary>
    /// Emits the 1-3 ankle-high stones sharing a pebble cell: floor-sitting cubes sized and
    /// spun per stone (see PebbleGenerator), with the stone tile cropped to their size so
    /// texel density matches full cubes. UVs come from the unrotated corners, so the texture
    /// stays put while the geometry spins. The bottom face is skipped: it sits coplanar on
    /// the ground's top face, which still draws because pebbles never occlude.
    /// </summary>
    internal static void EmitPebbles(int worldX0, int worldZ0, List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        byte[] padded, byte[] paddedState, int pIdx, byte[] sunLight, byte[] blockLight,
        int x, int y, int z, BlockType type, Vector3 basePos)
    {
        var worldX = worldX0 + x;
        var worldZ = worldZ0 + z;
        var maxCount = type.StackMax >= 2 ? type.StackMax : 3;
        var count = PebbleGenerator.CountWithState(worldX, worldZ, paddedState[pIdx], maxCount);
        var obsidian = PebbleGenerator.ObsidianCountWithState(worldX, worldZ, paddedState[pIdx], maxCount);
        var explicitPile = PebbleGenerator.TryDecode(paddedState[pIdx], maxCount, out _, out _);
        var obsidianType = BlockType.ById[PebbleGenerator.ObsidianBlock];
        var variant = PebbleGenerator.VariantAt(worldX, worldZ);

        Span<FaceLight> lights = stackalloc FaceLight[6];
        var lightBase = LightIdx(x, y, z);
        for (var i = 0; i < 6; i++)
        {
            // A face backed against rock or wood (a slope, a trunk) must not sample the
            // neighbor's zero light, or it renders black: light it from the pebble's own
            // cell instead, which sits open to the sky above the ground it rests on.
            lights[i] = EmitterFaceLight(i, padded, pIdx, sunLight, blockLight, lightBase, lightBase);
        }

        for (var b = 0; b < count; b++)
        {
            PebbleGenerator.GetCenter(count, variant, b, out var cx, out var cz);
            var s = PebbleGenerator.SizeAt(worldX, worldZ, b);
            var angle = PebbleGenerator.AngleAt(worldX, worldZ, b);
            var cos = MathF.Cos(angle);
            var sin = MathF.Sin(angle);
            var half = s * 0.5f;
            var min = new Vector3(cx - half, 0f, cz - half);
            var max = new Vector3(cx + half, s, cz + half);
            for (var face = 0; face < 6; face++)
            {
                if (face == 5) continue;
                var isObsidian = explicitPile ? b < obsidian : PebbleGenerator.IsObsidianAt(worldX, worldZ, b);
                var tile = face == 4
                    ? (isObsidian ? obsidianType?.Top ?? type.Top : type.Top)
                    : (isObsidian ? obsidianType?.Side ?? type.Side : type.Side);
                BoxFaceCorners(min, max, face, out var p0, out var p1, out var p2, out var p3);
                var normal = CubeFaces[face].normal;
                EmitQuad(vertices, indices, ref indexOffset, basePos,
                    SpinY(p0, cx, cz, cos, sin), SpinY(p1, cx, cz, cos, sin),
                    SpinY(p2, cx, cz, cos, sin), SpinY(p3, cx, cz, cos, sin),
                    SpinY(normal, 0f, 0f, cos, sin),
                    CropUv(face, p0), CropUv(face, p1), CropUv(face, p2), CropUv(face, p3), tile,
                    ShadeLut[(face << 2) | 3], lights[face]);
            }
        }
    }

    /// <summary>
    /// Emits the 1-4 crossed sticks sharing a branch-pile cell: long thin logs laid across
    /// each other campfire-style (see BranchPileGenerator), bark on the long faces and the
    /// log-top rings on the cut ends, each spun about Y by its pile yaw. UVs come from the
    /// unrotated corners, so the texture stays put while the geometry spins. The bottom face
    /// is skipped: the lowest stick sits coplanar on the ground's top face, which still draws
    /// because branch piles never occlude.
    /// </summary>
    internal static void EmitBranchPile(int worldX0, int worldZ0, List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        byte[] padded, byte[] paddedState, int pIdx, byte[] sunLight, byte[] blockLight,
        int x, int y, int z, BlockType type, Vector3 basePos)
    {
        var worldX = worldX0 + x;
        var worldZ = worldZ0 + z;
        var maxCount = type.StackMax >= 2 ? type.StackMax : 4;
        var count = BranchPileGenerator.CountWithState(paddedState[pIdx], maxCount);
        var variant = BranchPileGenerator.VariantAt(worldX, worldZ);

        Span<FaceLight> lights = stackalloc FaceLight[6];
        var lightBase = LightIdx(x, y, z);
        for (var i = 0; i < 6; i++)
            lights[i] = EmitterFaceLight(i, padded, pIdx, sunLight, blockLight, lightBase, lightBase);

        for (var b = 0; b < count; b++)
        {
            BranchPileGenerator.GetCenter(worldX, worldZ, variant, b, out var cx, out var cz);
            var half = BranchPileGenerator.LengthAt(worldX, worldZ, b) * 0.5f;
            var halfThick = BranchPileGenerator.Thickness * 0.5f;
            var yaw = BranchPileGenerator.YawAt(worldX, worldZ, b);
            var cos = MathF.Cos(yaw);
            var sin = MathF.Sin(yaw);
            var y0 = BranchPileGenerator.LayerY(b);
            var min = new Vector3(cx - half, y0, cz - halfThick);
            var max = new Vector3(cx + half, y0 + BranchPileGenerator.Thickness, cz + halfThick);
            for (var face = 0; face < 6; face++)
            {
                if (face == 5) continue;
                var tile = face is 2 or 3 ? type.Top : type.Side;
                BoxFaceCorners(min, max, face, out var p0, out var p1, out var p2, out var p3);
                var normal = CubeFaces[face].normal;
                EmitQuad(vertices, indices, ref indexOffset, basePos,
                    SpinY(p0, cx, cz, cos, sin), SpinY(p1, cx, cz, cos, sin),
                    SpinY(p2, cx, cz, cos, sin), SpinY(p3, cx, cz, cos, sin),
                    SpinY(normal, 0f, 0f, cos, sin),
                    CropUv(face, p0), CropUv(face, p1), CropUv(face, p2), CropUv(face, p3), tile,
                    ShadeLut[(face << 2) | 3], lights[face]);
            }
        }
    }

    /// <summary>
    /// Emits a snow layer cell as one box from the floor up to its depth (see
    /// <see cref="SnowShape"/>): the state byte holds 1..StackMax layers and the box is that
    /// fraction of a block tall, so a full stack is a full cube. The bottom face is skipped unless
    /// the cell below cannot occlude, since a layer normally rests on ground that draws its own top.
    /// </summary>
    internal static void EmitSnowLayers(int worldX0, int worldY0, int worldZ0, List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        byte[] padded, byte[] paddedState, int pIdx, byte[] sunLight, byte[] blockLight,
        int x, int y, int z, BlockType type, Vector3 basePos)
    {
        var worldX = worldX0 + x;
        var worldZ = worldZ0 + z;
        var maxCount = type.StackMax >= 2 ? type.StackMax : 16;
        var count = SnowShape.CountWithState(worldX, worldY0 + y, worldZ, paddedState[pIdx], maxCount);
        var height = SnowShape.Height(count, maxCount);

        Span<FaceLight> lights = stackalloc FaceLight[6];
        var lightBase = LightIdx(x, y, z);
        for (var i = 0; i < 6; i++)
            lights[i] = EmitterFaceLight(i, padded, pIdx, sunLight, blockLight, lightBase, lightBase);

        var drawBottom = count >= maxCount && !BlockType.Occludes(padded[pIdx + FaceNeighborDelta[5]]);
        var min = new Vector3(0f, 0f, 0f);
        var max = new Vector3(1f, height, 1f);
        for (var face = 0; face < 6; face++)
        {
            if (face == 5 && !drawBottom) continue;
            var tile = face == 4 ? type.Top : face == 5 ? type.Bottom : type.Side;
            BoxFaceCorners(min, max, face, out var p0, out var p1, out var p2, out var p3);
            EmitQuad(vertices, indices, ref indexOffset, basePos, p0, p1, p2, p3,
                CubeFaces[face].normal,
                CropUv(face, p0), CropUv(face, p1), CropUv(face, p2), CropUv(face, p3), tile,
                ShadeLut[(face << 2) | 3], lights[face]);
        }
    }

    /// <summary>Rotates a point about (cx, cz) by a Y spin; normals pass through with cx = cz = 0.</summary>
    internal static Vector3 SpinY(Vector3 p, float cx, float cz, float cos, float sin)
    {
        var dx = p.X - cx;
        var dz = p.Z - cz;
        return new Vector3(cx + dx * cos + dz * sin, p.Y, cz - dx * sin + dz * cos);
    }

    /// <summary>
    /// Emits a thin <see cref="BlockRenderer.TreeWood"/> cell: a centered node box sized by its
    /// <see cref="WoodState"/> thickness and one square tube toward every neighbor it connects
    /// to (<see cref="TreeWoodShape.Connects"/>), at the thinner of the two widths, plus a
    /// same-width plug toward every solid occluding neighbor (<see cref="TreeWoodShape.PlugMask"/>)
    /// so a root or trunk on the ground meets it. Tubes and plugs carry
    /// only their four walls - the neighbor emits the matching tube from its side, so the two
    /// meet flush with no cap between them - and a node face is dropped where a tube or plug of its
    /// own full width leaves it. The geometry is exactly <see cref="TreeWoodShape.GetBoxes"/>,
    /// so the hitboxes cover what is drawn. UVs are cropped (<see cref="CropUv"/>), so bark
    /// keeps the atlas texel density at every thickness.
    /// </summary>
    internal static void EmitTreeWood(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        byte[] padded, byte[] paddedState, int pIdx, byte[] sunLight, byte[] blockLight,
        int x, int y, int z, BlockType type, Vector3 basePos)
    {
        var state = paddedState[pIdx];
        Span<byte> nbIds = stackalloc byte[6];
        Span<byte> nbStates = stackalloc byte[6];
        Span<FaceLight> lights = stackalloc FaceLight[6];
        var lightBase = LightIdx(x, y, z);
        for (var i = 0; i < 6; i++)
        {
            nbIds[i] = padded[pIdx + FaceNeighborDelta[i]];
            nbStates[i] = paddedState[pIdx + FaceNeighborDelta[i]];
            // Backed against an occluder, the cell's own light (like pebbles), so a limb
            // pressed against rock never goes black
            lights[i] = EmitterFaceLight(i, padded, pIdx, sunLight, blockLight, lightBase, lightBase);
        }

        var mask = TreeWoodShape.ConnectionMask(type.Id, state, nbIds, nbStates);
        var plugs = TreeWoodShape.PlugMask(state, nbIds);
        var half = TreeWoodShape.HalfWidth(WoodState.Thickness(state));
        TreeWoodShape.NodeBox(half, out var nodeMin, out var nodeMax);
        for (var face = 0; face < 6; face++)
        {
            // A tube or plug as wide as the node continues its face, so that face is its mouth
            if ((plugs & 1 << face) != 0) continue;
            if ((mask & 1 << face) != 0 && TreeWoodShape.TubeHalf(state, nbStates[face]) >= half) continue;
            var tile = face == 4 ? type.Top : face == 5 ? type.Bottom : type.Side;
            EmitBoxFace(vertices, indices, ref indexOffset, basePos, nodeMin, nodeMax, face,
                tile, ShadeLut[(face << 2) | 3], lights[face]);
        }

        for (var dir = 0; dir < 6; dir++)
        {
            var connected = (mask & 1 << dir) != 0;
            if (!connected && (plugs & 1 << dir) == 0) continue;
            var tubeHalf = connected ? TreeWoodShape.TubeHalf(state, nbStates[dir]) : half;
            TreeWoodShape.TubeBox(dir, half, tubeHalf, out var min, out var max);
            var opposite = TreeWoodShape.Opposite(dir);
            for (var face = 0; face < 6; face++)
            {
                if (face == dir || face == opposite) continue;
                EmitBoxFace(vertices, indices, ref indexOffset, basePos, min, max, face,
                    type.Side, ShadeLut[(face << 2) | 3], lights[face]);
            }
        }
    }

    private const int RimPx = WoodState.PixelsPerBlock;

    // Upper bound on the quads one beveled shell emits: each cap is at most two segments per
    // pixel row, each box at most two X walls, and each Z boundary at most four wall segments
    private const int MaxRimQuads = 4 * RimPx + 6 * TreeWoodShape.MaxRimBoxes;

    /// <summary>
    /// <see cref="TreeWoodShape.RimCornerMask"/> of the cell at padded index
    /// <paramref name="idx"/>, read out of the snapshot. Only the four horizontal neighbors are
    /// read (the diagonals never change a cell's bevel, because a cell only ever carves its own
    /// material), so <paramref name="idx"/> must be a cell whose horizontal neighbors lie inside
    /// the 18^3 snapshot: the meshed cell itself or its vertical neighbors.
    /// </summary>
    internal static int RimMaskInSnapshot(byte[] padded, byte[] paddedState, int idx, byte id)
    {
        if (!TreeWoodShape.CanBevel(id, paddedState[idx])) return 0;
        Span<byte> ids = stackalloc byte[6];
        Span<byte> states = stackalloc byte[6];
        for (var i = 0; i < 4; i++)
        {
            ids[i] = padded[idx + FaceNeighborDelta[i]];
            states[i] = paddedState[idx + FaceNeighborDelta[i]];
        }

        return TreeWoodShape.RimCornerMask(id, paddedState[idx], ids, states);
    }

    /// <summary>
    /// Whether the full-cube <paramref name="id"/> cell at padded index <paramref name="nbIdx"/>
    /// fills its whole face toward <paramref name="side"/> (the direction from it to the viewing
    /// cell): <see cref="TreeWoodShape.RimSideFull"/> of its rim mask, read without leaving the
    /// snapshot. A side face is carved only at a corner whose two edge neighbors are both
    /// missing, and the viewing cell is one of the two for both corners on that side, so the
    /// neighbor's far side is never read; a cap needs the whole mask, whose horizontal reads
    /// stay inside the snapshot for a vertical neighbor.
    /// </summary>
    internal static bool RimFaceFull(byte[] padded, byte[] paddedState, int nbIdx, byte id, int side)
    {
        if (!TreeWoodShape.CanBevel(id, paddedState[nbIdx])) return true;
        if (side >= 4) return RimMaskInSnapshot(padded, paddedState, nbIdx, id) == 0;
        var toward = nbIdx + FaceNeighborDelta[side];
        if (TreeWoodShape.IsRimMass(id, padded[toward], paddedState[toward])) return true;
        // The two edge neighbors across the other axis: X for a Z side, Z for an X side
        var across = side < 2 ? 2 : 0;
        var a = nbIdx + FaceNeighborDelta[across];
        var b = nbIdx + FaceNeighborDelta[across + 1];
        return TreeWoodShape.IsRimMass(id, padded[a], paddedState[a])
               && TreeWoodShape.IsRimMass(id, padded[b], paddedState[b]);
    }

    /// <summary>
    /// Whether the rim cell at <paramref name="pIdx"/> hides its cell-boundary face toward
    /// side <paramref name="face"/>: an occluder does, and so does a same-wood full cube whose
    /// face toward this cell is whole (<see cref="RimFaceFull"/>; this cell is rim mass for it,
    /// so a horizontal one always is, and a detached log is never carved).
    /// </summary>
    internal static bool RimBoundaryHidden(byte[] padded, byte[] paddedState, int pIdx, byte id, int face)
    {
        var nbIdx = pIdx + FaceNeighborDelta[face];
        var nb = padded[nbIdx];
        if (BlockType.Occludes(nb)) return true;
        return nb == id && WoodState.IsFullCube(paddedState[nbIdx])
               && RimFaceFull(padded, paddedState, nbIdx, id, TreeWoodShape.Opposite(face));
    }

    // Per Z pixel row, the covered X pixel span [lo, hi) of a rim cell's boxes
    internal static void RimRows(ReadOnlySpan<BlockHitbox> boxes, Span<int> lo, Span<int> hi)
    {
        foreach (var box in boxes)
        {
            var z0 = (int)MathF.Round(box.Min.Z * RimPx);
            var z1 = (int)MathF.Round(box.Max.Z * RimPx);
            var x0 = (int)MathF.Round(box.Min.X * RimPx);
            var x1 = (int)MathF.Round(box.Max.X * RimPx);
            for (var z = z0; z < z1; z++)
            {
                lo[z] = x0;
                hi[z] = x1;
            }
        }
    }

    /// <summary>
    /// Emits a beveled pixel-rim trunk shell (<see cref="TreeWoodShape.CanBevel"/> with a
    /// non-zero corner <paramref name="mask"/>): the full-height <see cref="TreeWoodShape.RimBoxes"/>
    /// stair-stepping each carved corner, which are exactly the cell's hitboxes, so the raycast,
    /// the outline and collision match what is drawn. Faces on the cell boundary cull like a
    /// cube's (<see cref="RimBoundaryHidden"/>); the X walls inside the cell and the Z steps
    /// between two boxes are always drawn, the steps only where one box sticks out past its
    /// neighbor. Top and bottom draw only the part of the footprint that the same-wood cell
    /// above or below does not cover, so a stack of equal bevels shows no buried caps and a
    /// shell under a narrower one keeps its exposed rim.
    /// </summary>
    internal static void EmitRimWood(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        byte[] padded, byte[] paddedState, int pIdx, byte[] sunLight, byte[] blockLight,
        int x, int y, int z, BlockType type, Vector3 basePos, int mask)
    {
        if (vertices.Count > ushort.MaxValue - 4 * MaxRimQuads) return;
        var id = type.Id;
        Span<FaceLight> lights = stackalloc FaceLight[6];
        var lightBase = LightIdx(x, y, z);
        for (var i = 0; i < 6; i++)
            lights[i] = EmitterFaceLight(i, padded, pIdx, sunLight, blockLight, lightBase, lightBase);

        Span<BlockHitbox> boxes = stackalloc BlockHitbox[TreeWoodShape.MaxRimBoxes];
        var count = TreeWoodShape.RimBoxes(mask, boxes);
        boxes = boxes[..count];
        Span<int> rowLo = stackalloc int[RimPx];
        Span<int> rowHi = stackalloc int[RimPx];
        RimRows(boxes, rowLo, rowHi);

        // Top and bottom: this cell's rows minus the rows of the same-wood cell beyond the cap
        Span<BlockHitbox> coverBoxes = stackalloc BlockHitbox[TreeWoodShape.MaxRimBoxes];
        Span<int> coverLo = stackalloc int[RimPx];
        Span<int> coverHi = stackalloc int[RimPx];
        for (var face = 4; face <= 5; face++)
        {
            var nbIdx = pIdx + FaceNeighborDelta[face];
            var nb = padded[nbIdx];
            if (BlockType.Occludes(nb)) continue;
            var covered = false;
            if (nb == id && WoodState.IsFullCube(paddedState[nbIdx]))
            {
                var coverMask = RimMaskInSnapshot(padded, paddedState, nbIdx, id);
                if (coverMask == 0) continue; // a whole cube face buries the cap entirely
                RimRows(coverBoxes[..TreeWoodShape.RimBoxes(coverMask, coverBoxes)], coverLo, coverHi);
                covered = true;
            }

            var tile = face == 4 ? type.Top : type.Bottom;
            var shade = ShadeLut[(face << 2) | 3];
            var runStart = 0;
            int a0 = 0, a1 = 0, b0 = 0, b1 = 0;
            for (var row = 0; row <= RimPx; row++)
            {
                int s0 = 0, s1 = 0, t0 = 0, t1 = 0;
                if (row < RimPx)
                {
                    var lo = rowLo[row];
                    var hi = rowHi[row];
                    if (!covered)
                    {
                        s0 = lo;
                        s1 = hi;
                    }
                    else
                    {
                        // Up to two exposed segments: left of the cover and right of it
                        s0 = lo;
                        s1 = Math.Min(hi, coverLo[row]);
                        t0 = Math.Max(lo, coverHi[row]);
                        t1 = hi;
                    }

                    if (s1 <= s0) s0 = s1 = 0;
                    if (t1 <= t0) t0 = t1 = 0;
                    if (row > 0 && s0 == a0 && s1 == a1 && t0 == b0 && t1 == b1) continue;
                }

                if (row > 0)
                {
                    var z0 = runStart / (float)RimPx;
                    var z1 = row / (float)RimPx;
                    if (a1 > a0)
                        EmitBoxFace(vertices, indices, ref indexOffset, basePos,
                            new Vector3(a0 / (float)RimPx, 0f, z0), new Vector3(a1 / (float)RimPx, 1f, z1),
                            face, tile, shade, lights[face]);
                    if (b1 > b0)
                        EmitBoxFace(vertices, indices, ref indexOffset, basePos,
                            new Vector3(b0 / (float)RimPx, 0f, z0), new Vector3(b1 / (float)RimPx, 1f, z1),
                            face, tile, shade, lights[face]);
                }

                runStart = row;
                a0 = s0;
                a1 = s1;
                b0 = t0;
                b1 = t1;
            }
        }

        // X walls: a wall on the cell boundary culls like a cube face, one inside never does
        var hideMinX = RimBoundaryHidden(padded, paddedState, pIdx, id, 2);
        var hideMaxX = RimBoundaryHidden(padded, paddedState, pIdx, id, 3);
        var sideShadeMinX = ShadeLut[(2 << 2) | 3];
        var sideShadeMaxX = ShadeLut[(3 << 2) | 3];
        foreach (var box in boxes)
        {
            if (box.Min.X > 0f || !hideMinX)
                EmitBoxFace(vertices, indices, ref indexOffset, basePos, box.Min, box.Max, 2,
                    type.Side, sideShadeMinX, lights[2]);
            if (box.Max.X < 1f || !hideMaxX)
                EmitBoxFace(vertices, indices, ref indexOffset, basePos, box.Min, box.Max, 3,
                    type.Side, sideShadeMaxX, lights[3]);
        }

        // Z walls: the first and last are on the cell boundary; between two boxes only the part
        // of each box's wall that sticks out past the other is exposed
        var shadeMinZ = ShadeLut[(0 << 2) | 3];
        var shadeMaxZ = ShadeLut[(1 << 2) | 3];
        if (!RimBoundaryHidden(padded, paddedState, pIdx, id, 0))
            EmitBoxFace(vertices, indices, ref indexOffset, basePos, boxes[0].Min, boxes[0].Max, 0,
                type.Side, shadeMinZ, lights[0]);
        if (!RimBoundaryHidden(padded, paddedState, pIdx, id, 1))
            EmitBoxFace(vertices, indices, ref indexOffset, basePos, boxes[count - 1].Min, boxes[count - 1].Max, 1,
                type.Side, shadeMaxZ, lights[1]);
        for (var i = 0; i + 1 < count; i++)
        {
            var near = boxes[i];
            var far = boxes[i + 1];
            var zb = near.Max.Z;
            // The near box's +Z wall where the far box does not continue it, and vice versa
            EmitWallRemainder(vertices, indices, ref indexOffset, basePos, near.Min.X, near.Max.X,
                far.Min.X, far.Max.X, zb, 1, type.Side, shadeMaxZ, lights[1]);
            EmitWallRemainder(vertices, indices, ref indexOffset, basePos, far.Min.X, far.Max.X,
                near.Min.X, near.Max.X, zb, 0, type.Side, shadeMinZ, lights[0]);
        }
    }

    // A Z wall at depth zb spanning [x0, x1) minus the span [c0, c1) the adjacent box covers:
    // 0, 1 or 2 full-height segments
    internal static void EmitWallRemainder(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        Vector3 basePos, float x0, float x1, float c0, float c1, float zb, int face,
        ushort tile, byte shade, in FaceLight light)
    {
        var leftHi = MathF.Min(x1, c0);
        if (leftHi > x0)
            EmitBoxFace(vertices, indices, ref indexOffset, basePos,
                new Vector3(x0, 0f, zb), new Vector3(leftHi, 1f, zb), face, tile, shade, light);
        var rightLo = MathF.Max(x0, c1);
        if (x1 > rightLo)
            EmitBoxFace(vertices, indices, ref indexOffset, basePos,
                new Vector3(rightLo, 0f, zb), new Vector3(x1, 1f, zb), face, tile, shade, light);
    }

    /// <summary>One axis-aligned face of the [min, max] box (cell-local), with flat shading
    /// and cropped UVs (see <see cref="CropUv"/>).</summary>
    internal static void EmitBoxFace(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        Vector3 basePos, Vector3 min, Vector3 max, int face,
        ushort tile, byte shade, in FaceLight light)
    {
        BoxFaceCorners(min, max, face, out var a, out var b, out var c, out var d);
        EmitQuad(vertices, indices, ref indexOffset, basePos, a, b, c, d, CubeFaces[face].normal,
            CropUv(face, a), CropUv(face, b), CropUv(face, c), CropUv(face, d), tile,
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
