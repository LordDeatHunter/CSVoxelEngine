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

    // A notch's bite, in pixels of the 16 across a cell: the width across the face, and the band of
    // height it is cut over (centered). Its depth is WoodNotches.PixelsPerStep per step.
    private const int NotchWidthPx = 10;
    private const int NotchBottomPx = 4;
    private const int NotchTopPx = 12;
    private const int MaxNotchQuads = 4 + 5 + 5;

    /// <summary>
    /// Emits a full-cube wood cell with the bite of an axe cut taken out of its side face
    /// <paramref name="notchFace"/> (0..3), <paramref name="step"/> steps deep (<see cref="WoodNotches"/>):
    /// the cube's other five faces whole (dropped where a neighbor hides them, as a plain cube's
    /// are), the notch face as the four bars around the bite, and the bite's five walls in the cut
    /// wood's own tile (the log's end grain). The hitboxes stay the whole cube; the notch is cosmetic.
    /// </summary>
    internal static void EmitNotched(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        byte[] padded, byte[] paddedState, int pIdx, byte[] sunLight, byte[] blockLight,
        int x, int y, int z, BlockType type, Vector3 basePos, int notchFace, int step)
    {
        if (vertices.Count > ushort.MaxValue - 4 * MaxNotchQuads) return;
        var id = type.Id;
        Span<FaceLight> lights = stackalloc FaceLight[6];
        var lightBase = LightIdx(x, y, z);
        for (var i = 0; i < 6; i++)
            lights[i] = EmitterFaceLight(i, padded, pIdx, sunLight, blockLight, lightBase, lightBase);

        // The five faces that stay whole: culled against an occluder or a same-wood full cube, like a cube's
        for (var face = 0; face < 6; face++)
        {
            if (face == notchFace) continue;
            var nbIdx = pIdx + ChunkFaceTables.FaceNeighborDelta[face];
            var nb = padded[nbIdx];
            if (BlockType.Occludes(nb) || (nb == id && WoodState.IsFullCube(paddedState[nbIdx]))) continue;
            var tile = face == 4 ? type.Top : face == 5 ? type.Bottom : type.Side;
            ChunkMesher.EmitBoxFace(vertices, indices, ref indexOffset, basePos, Vector3.Zero, Vector3.One, face,
                tile, ChunkFaceTables.ShadeLut[(face << 2) | 3], lights[face]);
        }

        const float Px = 1f / RimPx;
        var a = (RimPx - NotchWidthPx) / 2 * Px;
        var b = 1f - a;
        var yb = NotchBottomPx * Px;
        var yt = NotchTopPx * Px;
        var depth = Math.Min(step, WoodNotches.MaxSteps) * WoodNotches.PixelsPerStep * Px;
        var f = notchFace;
        var sideShade = ChunkFaceTables.ShadeLut[(f << 2) | 3];
        var light = lights[f];
        // The faces toward -u and +u of the notch face's frame (u runs along the face)
        var minusU = f < 2 ? 2 : 0;
        var plusU = minusU + 1;

        // The four bars on the notch face, at its outer surface (n = 0)
        EmitFrameFace(vertices, indices, ref indexOffset, basePos, f, 0f, a, 0f, 1f, 0f, 0f, f, type.Side, sideShade, light);
        EmitFrameFace(vertices, indices, ref indexOffset, basePos, f, b, 1f, 0f, 1f, 0f, 0f, f, type.Side, sideShade, light);
        EmitFrameFace(vertices, indices, ref indexOffset, basePos, f, a, b, 0f, yb, 0f, 0f, f, type.Side, sideShade, light);
        EmitFrameFace(vertices, indices, ref indexOffset, basePos, f, a, b, yt, 1f, 0f, 0f, f, type.Side, sideShade, light);

        // The bite's walls, lit from the open side: the back, the two sides, the floor and the ceiling
        EmitFrameFace(vertices, indices, ref indexOffset, basePos, f, a, b, yb, yt, depth, depth, f,
            type.Top, sideShade, light);
        EmitFrameFace(vertices, indices, ref indexOffset, basePos, f, a, a, yb, yt, 0f, depth, plusU,
            type.Top, ChunkFaceTables.ShadeLut[(plusU << 2) | 3], light);
        EmitFrameFace(vertices, indices, ref indexOffset, basePos, f, b, b, yb, yt, 0f, depth, minusU,
            type.Top, ChunkFaceTables.ShadeLut[(minusU << 2) | 3], light);
        EmitFrameFace(vertices, indices, ref indexOffset, basePos, f, a, b, yb, yb, 0f, depth, 4,
            type.Top, ChunkFaceTables.ShadeLut[(4 << 2) | 3], light);
        EmitFrameFace(vertices, indices, ref indexOffset, basePos, f, a, b, yt, yt, 0f, depth, 5,
            type.Top, ChunkFaceTables.ShadeLut[(5 << 2) | 3], light);
    }

    // One face of a box given in the notch face's frame: u along the face (0..1), y up, n the depth in
    // from the outer surface, mapped onto the cell for side face f (0 -Z, 1 +Z, 2 -X, 3 +X)
    private static void EmitFrameFace(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        Vector3 basePos, int f, float u0, float u1, float y0, float y1, float n0, float n1, int face,
        ushort tile, byte shade, in FaceLight light)
    {
        Vector3 min, max;
        switch (f)
        {
            case 0: min = new Vector3(u0, y0, n0); max = new Vector3(u1, y1, n1); break;
            case 1: min = new Vector3(u0, y0, 1f - n1); max = new Vector3(u1, y1, 1f - n0); break;
            case 2: min = new Vector3(n0, y0, u0); max = new Vector3(n1, y1, u1); break;
            default: min = new Vector3(1f - n1, y0, u0); max = new Vector3(1f - n0, y1, u1); break;
        }

        ChunkMesher.EmitBoxFace(vertices, indices, ref indexOffset, basePos, min, max, face, tile, shade, light);
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
