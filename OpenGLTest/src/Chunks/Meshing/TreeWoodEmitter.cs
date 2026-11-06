using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Data;
using VoxelEngine.WorldGen.Trees;
using static VoxelEngine.Chunks.Meshing.ChunkLighting;

namespace VoxelEngine.Chunks.Meshing;

/// <summary>
/// GL-free tree-wood mesh emission: a thin <c>tree_wood</c> cell's node box, connecting tubes
/// and ground plugs, and the stair-stepped pixel-rim bevel of a wide trunk's outer shells.
/// </summary>
internal static class TreeWoodEmitter
{
    /// <summary>
    /// Emits a thin <see cref="BlockRenderer.TreeWood"/> cell: a centered node box sized by its
    /// <see cref="WoodState"/> thickness and one square tube toward every neighbor it connects
    /// to (<see cref="TreeWoodShape.Connects"/>), at the thinner of the two widths, plus a
    /// same-width plug toward every solid occluding neighbor (<see cref="TreeWoodShape.PlugMask"/>)
    /// so a root or trunk on the ground meets it. Tubes and plugs carry
    /// only their four walls - the neighbor emits the matching tube from its side, so the two
    /// meet flush with no cap between them - and a node face is dropped where a tube or plug of its
    /// own full width leaves it. The geometry is exactly <see cref="TreeWoodShape.GetBoxes"/>,
    /// so the hitboxes cover what is drawn. UVs are cropped (<see cref="ChunkFaceTables.CropUv"/>), so bark
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
            nbIds[i] = padded[pIdx + ChunkFaceTables.FaceNeighborDelta[i]];
            nbStates[i] = paddedState[pIdx + ChunkFaceTables.FaceNeighborDelta[i]];
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
            ChunkMesher.EmitBoxFace(vertices, indices, ref indexOffset, basePos, nodeMin, nodeMax, face,
                tile, ChunkFaceTables.ShadeLut[(face << 2) | 3], lights[face]);
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
                ChunkMesher.EmitBoxFace(vertices, indices, ref indexOffset, basePos, min, max, face,
                    type.Side, ChunkFaceTables.ShadeLut[(face << 2) | 3], lights[face]);
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
            ids[i] = padded[idx + ChunkFaceTables.FaceNeighborDelta[i]];
            states[i] = paddedState[idx + ChunkFaceTables.FaceNeighborDelta[i]];
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
        var toward = nbIdx + ChunkFaceTables.FaceNeighborDelta[side];
        if (TreeWoodShape.IsRimMass(id, padded[toward], paddedState[toward])) return true;
        // The two edge neighbors across the other axis: X for a Z side, Z for an X side
        var across = side < 2 ? 2 : 0;
        var a = nbIdx + ChunkFaceTables.FaceNeighborDelta[across];
        var b = nbIdx + ChunkFaceTables.FaceNeighborDelta[across + 1];
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
        var nbIdx = pIdx + ChunkFaceTables.FaceNeighborDelta[face];
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
            var nbIdx = pIdx + ChunkFaceTables.FaceNeighborDelta[face];
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
            var shade = ChunkFaceTables.ShadeLut[(face << 2) | 3];
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
                        ChunkMesher.EmitBoxFace(vertices, indices, ref indexOffset, basePos,
                            new Vector3(a0 / (float)RimPx, 0f, z0), new Vector3(a1 / (float)RimPx, 1f, z1),
                            face, tile, shade, lights[face]);
                    if (b1 > b0)
                        ChunkMesher.EmitBoxFace(vertices, indices, ref indexOffset, basePos,
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
        var sideShadeMinX = ChunkFaceTables.ShadeLut[(2 << 2) | 3];
        var sideShadeMaxX = ChunkFaceTables.ShadeLut[(3 << 2) | 3];
        foreach (var box in boxes)
        {
            if (box.Min.X > 0f || !hideMinX)
                ChunkMesher.EmitBoxFace(vertices, indices, ref indexOffset, basePos, box.Min, box.Max, 2,
                    type.Side, sideShadeMinX, lights[2]);
            if (box.Max.X < 1f || !hideMaxX)
                ChunkMesher.EmitBoxFace(vertices, indices, ref indexOffset, basePos, box.Min, box.Max, 3,
                    type.Side, sideShadeMaxX, lights[3]);
        }

        // Z walls: the first and last are on the cell boundary; between two boxes only the part
        // of each box's wall that sticks out past the other is exposed
        var shadeMinZ = ChunkFaceTables.ShadeLut[(0 << 2) | 3];
        var shadeMaxZ = ChunkFaceTables.ShadeLut[(1 << 2) | 3];
        if (!RimBoundaryHidden(padded, paddedState, pIdx, id, 0))
            ChunkMesher.EmitBoxFace(vertices, indices, ref indexOffset, basePos, boxes[0].Min, boxes[0].Max, 0,
                type.Side, shadeMinZ, lights[0]);
        if (!RimBoundaryHidden(padded, paddedState, pIdx, id, 1))
            ChunkMesher.EmitBoxFace(vertices, indices, ref indexOffset, basePos, boxes[count - 1].Min, boxes[count - 1].Max, 1,
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

    // The bite of a chopped cell, in pixels of the 16 across a cell: the band of height it is cut over
    // (the cells above and below keep their own faces) and how far it goes in. Its width along the face
    // is the eaten share (WoodDamage.EatenColumns).
    private const int BiteBottomPx = 3;
    private const int BiteTopPx = 13;
    private const int BiteDepthPx = 4;
    private const int MaxBiteQuads = 40;

    /// <summary>
    /// Emits a full-cube wood cell whose side face <paramref name="biteFace"/> (0..3) has been eaten away
    /// over <paramref name="eatenColumns"/> of its 16 columns, from the viewer's left edge toward the right
    /// (<see cref="WoodDamage"/>): the cube minus one box in a band of the face, every cell face
    /// kept whole except where the box reaches it (the face is then drawn as the bars around the hole, so a
    /// cut that runs to a side edge also opens the neighboring face), the box's inner walls in the cut wood's
    /// own tile (the log's end grain), and a cap where the box meets a face a neighbor hides, so the bite is
    /// never see-through. The hitboxes stay the whole cube; the bite is cosmetic.
    /// </summary>
    internal static void EmitEaten(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        byte[] padded, byte[] paddedState, int pIdx, byte[] sunLight, byte[] blockLight,
        int x, int y, int z, BlockType type, Vector3 basePos, int biteFace, int eatenColumns)
    {
        if (vertices.Count > ushort.MaxValue - 4 * MaxBiteQuads) return;
        var id = type.Id;
        Span<FaceLight> lights = stackalloc FaceLight[6];
        var lightBase = LightIdx(x, y, z);
        for (var i = 0; i < 6; i++)
            lights[i] = EmitterFaceLight(i, padded, pIdx, sunLight, blockLight, lightBase, lightBase);

        const float Px = 1f / RimPx;
        var width = Math.Clamp(eatenColumns, 1, WoodDamage.FaceColumns) * Px;
        var depth = BiteDepthPx * Px;
        var y0 = BiteBottomPx * Px;
        var y1 = BiteTopPx * Px;
        // The viewer's left of each face, along its +x/+z running axis: the +Z and -X faces start at 0, the others at 1
        var u0 = biteFace is 1 or 2 ? 0f : 1f - width;
        var u1 = u0 + width;
        Vector3 bmin, bmax;
        switch (biteFace)
        {
            case 0: bmin = new Vector3(u0, y0, 0f); bmax = new Vector3(u1, y1, depth); break;
            case 1: bmin = new Vector3(u0, y0, 1f - depth); bmax = new Vector3(u1, y1, 1f); break;
            case 2: bmin = new Vector3(0f, y0, u0); bmax = new Vector3(depth, y1, u1); break;
            default: bmin = new Vector3(1f - depth, y0, u0); bmax = new Vector3(1f, y1, u1); break;
        }

        var biteLight = lights[biteFace];
        for (var face = 0; face < 6; face++)
        {
            var touches = BoxTouchesFace(bmin, bmax, face);
            var nbIdx = pIdx + ChunkFaceTables.FaceNeighborDelta[face];
            var nb = padded[nbIdx];
            var hidden = BlockType.Occludes(nb) || (nb == id && WoodState.IsFullCube(paddedState[nbIdx]));
            var tile = face == 4 ? type.Top : face == 5 ? type.Bottom : type.Side;
            var shade = ChunkFaceTables.ShadeLut[(face << 2) | 3];
            if (hidden)
            {
                // The neighbor hides this face: where the bite reaches it, cap the bite so it is not see-through
                if (touches)
                    EmitCap(vertices, indices, ref indexOffset, basePos, bmin, bmax, face, type.Top, biteLight);
                continue;
            }

            if (touches)
                EmitFaceMinusRect(vertices, indices, ref indexOffset, basePos, face, tile, shade, lights[face], bmin, bmax);
            else
                ChunkMesher.EmitBoxFace(vertices, indices, ref indexOffset, basePos, Vector3.Zero, Vector3.One, face, tile,
                    shade, lights[face]);
        }

        // The walls of the bite that stand inside the cell, facing into the open box
        if (bmin.X > 0f) EmitWall(vertices, indices, ref indexOffset, basePos, bmin, bmax, 3, type.Top, biteLight);
        if (bmax.X < 1f) EmitWall(vertices, indices, ref indexOffset, basePos, bmin, bmax, 2, type.Top, biteLight);
        if (bmin.Z > 0f) EmitWall(vertices, indices, ref indexOffset, basePos, bmin, bmax, 1, type.Top, biteLight);
        if (bmax.Z < 1f) EmitWall(vertices, indices, ref indexOffset, basePos, bmin, bmax, 0, type.Top, biteLight);
        if (bmin.Y > 0f) EmitWall(vertices, indices, ref indexOffset, basePos, bmin, bmax, 4, type.Top, biteLight);
        if (bmax.Y < 1f) EmitWall(vertices, indices, ref indexOffset, basePos, bmin, bmax, 5, type.Top, biteLight);
    }

    // Whether the box reaches the plane of the cell's face (a box that stops short leaves it whole)
    private static bool BoxTouchesFace(Vector3 bmin, Vector3 bmax, int face) => face switch
    {
        0 => bmin.Z <= 0f,
        1 => bmax.Z >= 1f,
        2 => bmin.X <= 0f,
        3 => bmax.X >= 1f,
        4 => bmax.Y >= 1f,
        _ => bmin.Y <= 0f,
    };

    // One wall of the box with its normal <paramref name="face"/>, sitting at the box side that faces the
    // other way (the side of the box on the solid material's edge)
    private static void EmitWall(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        Vector3 basePos, Vector3 bmin, Vector3 bmax, int face, ushort tile, in FaceLight light)
    {
        // The normal +X (3) wall is the box's min-X side, -X (2) the max-X side, and so on
        var min = bmin;
        var max = bmax;
        switch (face)
        {
            case 3: max.X = bmin.X; break;
            case 2: min.X = bmax.X; break;
            case 1: max.Z = bmin.Z; break;
            case 0: min.Z = bmax.Z; break;
            case 4: max.Y = bmin.Y; break;
            default: min.Y = bmax.Y; break;
        }

        ChunkMesher.EmitBoxFace(vertices, indices, ref indexOffset, basePos, min, max, face, tile,
            ChunkFaceTables.ShadeLut[(face << 2) | 3], light);
    }

    // The bite's wall on the plane of a cell face its neighbor hides: its normal points back into the box
    private static void EmitCap(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        Vector3 basePos, Vector3 bmin, Vector3 bmax, int cellFace, ushort tile, in FaceLight light)
    {
        var min = bmin;
        var max = bmax;
        switch (cellFace)
        {
            case 0: max.Z = min.Z; break;
            case 1: min.Z = max.Z; break;
            case 2: max.X = min.X; break;
            case 3: min.X = max.X; break;
            case 4: min.Y = max.Y; break;
            default: max.Y = min.Y; break;
        }

        var face = cellFace ^ 1;
        ChunkMesher.EmitBoxFace(vertices, indices, ref indexOffset, basePos, min, max, face, tile,
            ChunkFaceTables.ShadeLut[(face << 2) | 3], light);
    }

    // A whole cell face minus the rectangle the box leaves on its plane: the (up to four) bars around the hole
    private static void EmitFaceMinusRect(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        Vector3 basePos, int face, ushort tile, byte shade, in FaceLight light, Vector3 hmin, Vector3 hmax)
    {
        // The face's in-plane axes (0 x, 1 y, 2 z) and the plane's own coordinate
        var (pa, qa, na) = face switch { 0 or 1 => (0, 1, 2), 2 or 3 => (2, 1, 0), _ => (0, 2, 1) };
        var plane = face is 1 or 3 or 4 ? 1f : 0f;
        var p0 = Axis(hmin, pa);
        var p1 = Axis(hmax, pa);
        var q0 = Axis(hmin, qa);
        var q1 = Axis(hmax, qa);

        // Below and above the hole across the whole width, then the strips left and right of it
        Bar(vertices, indices, ref indexOffset, basePos, face, tile, shade, light, pa, qa, na, plane, 0f, 1f, 0f, q0);
        Bar(vertices, indices, ref indexOffset, basePos, face, tile, shade, light, pa, qa, na, plane, 0f, 1f, q1, 1f);
        Bar(vertices, indices, ref indexOffset, basePos, face, tile, shade, light, pa, qa, na, plane, 0f, p0, q0, q1);
        Bar(vertices, indices, ref indexOffset, basePos, face, tile, shade, light, pa, qa, na, plane, p1, 1f, q0, q1);
    }

    private static void Bar(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset, Vector3 basePos,
        int face, ushort tile, byte shade, in FaceLight light, int pa, int qa, int na, float plane,
        float p0, float p1, float q0, float q1)
    {
        if (p1 <= p0 || q1 <= q0) return;
        ChunkMesher.EmitBoxFace(vertices, indices, ref indexOffset, basePos,
            WithAxes(pa, p0, qa, q0, na, plane), WithAxes(pa, p1, qa, q1, na, plane), face, tile, shade, light);
    }

    private static float Axis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    private static Vector3 WithAxes(int a, float av, int b, float bv, int c, float cv)
    {
        Span<float> s = stackalloc float[3];
        s[a] = av;
        s[b] = bv;
        s[c] = cv;
        return new Vector3(s[0], s[1], s[2]);
    }

    // A Z wall at depth zb spanning [x0, x1) minus the span [c0, c1) the adjacent box covers:
    // 0, 1 or 2 full-height segments
    internal static void EmitWallRemainder(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        Vector3 basePos, float x0, float x1, float c0, float c1, float zb, int face,
        ushort tile, byte shade, in FaceLight light)
    {
        var leftHi = MathF.Min(x1, c0);
        if (leftHi > x0)
            ChunkMesher.EmitBoxFace(vertices, indices, ref indexOffset, basePos,
                new Vector3(x0, 0f, zb), new Vector3(leftHi, 1f, zb), face, tile, shade, light);
        var rightLo = MathF.Max(x0, c1);
        if (x1 > rightLo)
            ChunkMesher.EmitBoxFace(vertices, indices, ref indexOffset, basePos,
                new Vector3(rightLo, 0f, zb), new Vector3(x1, 1f, zb), face, tile, shade, light);
    }
}
