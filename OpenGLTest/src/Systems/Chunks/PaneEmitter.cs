using System;
using System.Collections.Generic;
using System.Numerics;
using static silkgltriangle.Chunk;
using static silkgltriangle.ChunkLighting;

namespace silkgltriangle;

/// <summary>
/// Emits a <see cref="BlockRenderer.Pane"/> cell from the padded snapshot: the centre post plus an
/// arm toward every neighbour <see cref="PaneShape.ConnectsTo"/> reaches, exactly the boxes
/// <see cref="PaneShape.Boxes"/> hands the raycast and collision. An arm reaches the cell edge
/// and carries no end cap, and the post drops its side where an arm leaves it, so two connected
/// panes meet flush with no face between them, and an arm into a wall stops against it. Tops and
/// bottoms cull against an occluder, and against a same-block pane above or below wherever that
/// pane has the same post or arm, so a stack of panes shows no buried faces either. Faces are lit
/// like the other emitters' (<see cref="ChunkLighting.EmitterFaceLight"/>), shaded flat, with UVs
/// cropped from the block's tiles so the glass continues across post and arms.
/// </summary>
internal static class PaneEmitter
{
    /// <summary>Most quads one pane emits: the post's six faces plus two walls, a top and a
    /// bottom for each of the four arms.</summary>
    internal const int MaxQuads = 6 + PaneShape.Directions * 4;

    // Bit of a cap mask standing for the post (bits 0-3 are the arms, as in an arm mask)
    private const int PostBit = 1 << PaneShape.Directions;

    /// <summary>The arm mask of the pane at padded index <paramref name="idx"/>, read from its four
    /// horizontal neighbours, which must lie inside the 18^3 snapshot: the meshed cell itself or
    /// the cell right above or below it.</summary>
    internal static int MaskInSnapshot(byte[] padded, byte[] paddedState, int idx)
    {
        Span<byte> ids = stackalloc byte[PaneShape.Directions];
        Span<byte> states = stackalloc byte[PaneShape.Directions];
        for (var dir = 0; dir < PaneShape.Directions; dir++)
        {
            ids[dir] = padded[idx + FaceNeighborDelta[dir]];
            states[dir] = paddedState[idx + FaceNeighborDelta[dir]];
        }
        return PaneShape.ConnectionMask(ids, states);
    }

    /// <summary>
    /// Which of this pane's top (<paramref name="face"/> 4) or bottom (5) faces are hidden by the
    /// cell beyond them, as a cap mask (arm bits plus <see cref="PostBit"/>): all of them under an
    /// occluder, the post and the shared arms under a same-block pane fused with this one (the
    /// block's <see cref="BlockType.CullSelf"/>), none otherwise.
    /// </summary>
    private static int HiddenCaps(byte[] padded, byte[] paddedState, int pIdx, BlockType type, int face)
    {
        var nbIdx = pIdx + FaceNeighborDelta[face];
        var nb = padded[nbIdx];
        if (BlockType.Occludes(nb)) return PostBit | 15;
        if (type.CullSelf && nb == type.Id) return PostBit | MaskInSnapshot(padded, paddedState, nbIdx);
        return 0;
    }

    internal static void EmitPane(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        byte[] padded, byte[] paddedState, int pIdx, byte[] sunLight, byte[] blockLight,
        int x, int y, int z, BlockType type, Vector3 basePos)
    {
        // Meshes are indexed with ushort: stop emitting into a full list instead of wrapping
        if (vertices.Count > ushort.MaxValue - 4 * MaxQuads) return;

        var mask = MaskInSnapshot(padded, paddedState, pIdx);
        var hiddenTop = HiddenCaps(padded, paddedState, pIdx, type, 4);
        var hiddenBottom = HiddenCaps(padded, paddedState, pIdx, type, 5);

        Span<FaceLight> lights = stackalloc FaceLight[6];
        var lightBase = LightIdx(x, y, z);
        for (var i = 0; i < 6; i++)
            lights[i] = EmitterFaceLight(i, padded, pIdx, sunLight, blockLight, lightBase, lightBase);

        // The post: a side only where no arm leaves it, its caps where nothing covers them
        PaneShape.PostBox(out var postMin, out var postMax);
        for (var face = 0; face < PaneShape.Directions; face++)
            if ((mask & 1 << face) == 0)
                Face(vertices, indices, ref indexOffset, basePos, postMin, postMax, face, type.Side, lights);
        if ((hiddenTop & PostBit) == 0)
            Face(vertices, indices, ref indexOffset, basePos, postMin, postMax, 4, type.Top, lights);
        if ((hiddenBottom & PostBit) == 0)
            Face(vertices, indices, ref indexOffset, basePos, postMin, postMax, 5, type.Bottom, lights);

        // Each arm: its two long walls (across X for a north/south arm, across Z for a west/east
        // one) and its caps; never an end, which meets the neighbour it connects to
        for (var dir = 0; dir < PaneShape.Directions; dir++)
        {
            if ((mask & 1 << dir) == 0) continue;
            PaneShape.ArmBox(dir, out var min, out var max);
            var wallA = dir < 2 ? 2 : 0;
            Face(vertices, indices, ref indexOffset, basePos, min, max, wallA, type.Side, lights);
            Face(vertices, indices, ref indexOffset, basePos, min, max, wallA + 1, type.Side, lights);
            if ((hiddenTop & 1 << dir) == 0)
                Face(vertices, indices, ref indexOffset, basePos, min, max, 4, type.Top, lights);
            if ((hiddenBottom & 1 << dir) == 0)
                Face(vertices, indices, ref indexOffset, basePos, min, max, 5, type.Bottom, lights);
        }
    }

    private static void Face(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        Vector3 basePos, Vector3 min, Vector3 max, int face, ushort tile, ReadOnlySpan<FaceLight> lights) =>
        ChunkMesher.EmitBoxFace(vertices, indices, ref indexOffset, basePos, min, max, face, tile,
            ShadeLut[(face << 2) | 3], lights[face]);
}
