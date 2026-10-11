using System;
using System.Collections.Generic;
using System.Numerics;
using static silkgltriangle.Chunk;
using static silkgltriangle.ChunkLighting;

namespace silkgltriangle;

/// <summary>
/// Emits a <see cref="BlockRenderer.Pane"/> cell from the padded snapshot: the centre post plus an
/// arm toward every neighbour <see cref="PaneShape.ConnectsTo(PaneProfile, byte, byte)"/> reaches,
/// exactly the boxes <see cref="PaneProfile.Boxes"/> hands the raycast and collision, in the
/// block's own <see cref="PaneProfile"/> (post and arm widths, and the rails an arm fills). An arm
/// reaches the cell edge and carries no end cap, and the post drops its side where an arm covering
/// it whole leaves it, so two connected panes meet flush with no face between them, and an arm into
/// a wall stops against it. Tops and bottoms on the cell's top and bottom cull against an occluder,
/// and against a same-block pane above or below wherever that pane has the same post or arm, so a
/// stack of panes shows no buried faces either; a rail that stops short of the cell's top or bottom
/// always keeps its cap. Faces are lit like the other emitters'
/// (<see cref="ChunkLighting.EmitterFaceLight"/>), shaded flat, with UVs cropped from the block's
/// tiles so the texture continues across post and arms.
/// </summary>
internal static class PaneEmitter
{
    // Bit of a cap mask standing for the post (bits 0-3 are the arms, as in an arm mask)
    private const int PostBit = 1 << PaneShape.Directions;

    /// <summary>The arm mask of the pane at padded index <paramref name="idx"/>, in its own shape,
    /// read from its four horizontal neighbours, which must lie inside the 18^3 snapshot: the
    /// meshed cell itself or the cell right above or below it.</summary>
    internal static int MaskInSnapshot(byte[] padded, byte[] paddedState, int idx)
    {
        Span<byte> ids = stackalloc byte[PaneShape.Directions];
        Span<byte> states = stackalloc byte[PaneShape.Directions];
        for (var dir = 0; dir < PaneShape.Directions; dir++)
        {
            ids[dir] = padded[idx + FaceNeighborDelta[dir]];
            states[dir] = paddedState[idx + FaceNeighborDelta[dir]];
        }
        return PaneShape.ConnectionMask(PaneShape.ProfileOf(padded[idx]), ids, states);
    }

    /// <summary>
    /// Which of this pane's top (<paramref name="face"/> 4) or bottom (5) faces on the cell's own
    /// top or bottom are hidden by the cell beyond them, as a cap mask (arm bits plus
    /// <see cref="PostBit"/>): all of them under an occluder, the post and the shared arms under a
    /// same-block pane fused with this one (the block's <see cref="BlockType.CullSelf"/>), none
    /// otherwise.
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
        var profile = type.Pane ?? PaneProfile.Default;

        // Meshes are indexed with ushort: stop emitting into a full list instead of wrapping
        if (vertices.Count > ushort.MaxValue - 4 * profile.MaxQuads) return;

        var mask = MaskInSnapshot(padded, paddedState, pIdx);
        var hiddenTop = HiddenCaps(padded, paddedState, pIdx, type, 4);
        var hiddenBottom = HiddenCaps(padded, paddedState, pIdx, type, 5);

        Span<FaceLight> lights = stackalloc FaceLight[6];
        var lightBase = LightIdx(x, y, z);
        for (var i = 0; i < 6; i++)
            lights[i] = EmitterFaceLight(i, padded, pIdx, sunLight, blockLight, lightBase, lightBase);

        // The post: a side only where no arm covering it whole leaves it, its caps where nothing
        // covers them. A narrower or railed arm leaves part of the side showing, so the side stays
        // and the arm's open end butts against it
        profile.PostBox(out var postMin, out var postMax);
        for (var face = 0; face < PaneShape.Directions; face++)
            if ((mask & 1 << face) == 0 || !profile.FullArms)
                Face(vertices, indices, ref indexOffset, basePos, postMin, postMax, face, type.Side, lights);
        if ((hiddenTop & PostBit) == 0)
            Face(vertices, indices, ref indexOffset, basePos, postMin, postMax, 4, type.Top, lights);
        if ((hiddenBottom & PostBit) == 0)
            Face(vertices, indices, ref indexOffset, basePos, postMin, postMax, 5, type.Bottom, lights);

        // Each arm's rails: their two long walls (across X for a north/south arm, across Z for a
        // west/east one) and their caps; never an end, which meets the neighbour it connects to
        for (var dir = 0; dir < PaneShape.Directions; dir++)
        {
            if ((mask & 1 << dir) == 0) continue;
            var wallA = dir < 2 ? 2 : 0;
            for (var rail = 0; rail < profile.Rails.Length; rail++)
            {
                profile.ArmBox(dir, rail, out var min, out var max);
                Face(vertices, indices, ref indexOffset, basePos, min, max, wallA, type.Side, lights);
                Face(vertices, indices, ref indexOffset, basePos, min, max, wallA + 1, type.Side, lights);
                if (max.Y < 1f || (hiddenTop & 1 << dir) == 0)
                    Face(vertices, indices, ref indexOffset, basePos, min, max, 4, type.Top, lights);
                if (min.Y > 0f || (hiddenBottom & 1 << dir) == 0)
                    Face(vertices, indices, ref indexOffset, basePos, min, max, 5, type.Bottom, lights);
            }
        }
    }

    private static void Face(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        Vector3 basePos, Vector3 min, Vector3 max, int face, ushort tile, ReadOnlySpan<FaceLight> lights) =>
        ChunkMesher.EmitBoxFace(vertices, indices, ref indexOffset, basePos, min, max, face, tile,
            ShadeLut[(face << 2) | 3], lights[face]);
}
