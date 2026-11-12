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
/// The chop progress of a thin (node-and-tubes) tree-wood cell: the same slit a full-cube trunk shows
/// (<see cref="TreeWoodEmitter.EmitEaten"/>), cut into the cell's real shape. The slit is a band across
/// the node on the side face the first hit landed on, as wide as the cell and as deep as the damage
/// (<see cref="WoodDamage"/>, scaled to the node's own width so a thin twig is never severed before it
/// falls) and perpendicular to the wood's own run: a horizontal band across a vertical trunk or root, a
/// vertical one across a horizontal limb (the run is the axis of the cell's parent direction); every box of the cell (node, tubes, ground plug) has the band subtracted and is drawn whole,
/// the cut walls in the log's end grain. The geometry helpers are GL-free. The hitboxes stay the whole
/// shape; the bite is cosmetic.
/// </summary>
internal static class ThinWoodBite
{
    // The slit's height against the node's width, and at least a pixel
    private const float BandShare = 0.4f;
    private const float MinBand = 1f / WoodState.PixelsPerBlock;
    // A thin cell is a node plus up to six tubes, each cut into at most six boxes of six faces
    private const int MaxQuads = 7 * 6 * 6;

    /// <summary>Whether the mesh has room for the bite of one cell (its vertex budget is a ushort).</summary>
    internal static bool HasRoom(List<Vertex> vertices) => vertices.Count <= ushort.MaxValue - 4 * MaxQuads;

    /// <summary>
    /// The axis (0 = X, 1 = Y, 2 = Z) the wood of a thin cell runs along: that of its parent direction,
    /// vertical for a root. The bite is a slab across it.
    /// </summary>
    internal static int RunAxis(byte state) =>
        !WoodState.HasParentCell(state) ? 1 : WoodState.Parent(state) switch { 0 or 1 => 2, 2 or 3 => 0, _ => 1 };

    /// <summary>
    /// The cell-local box that is cut away from a thin cell of <paramref name="state"/> by a bite on
    /// side face <paramref name="biteFace"/> (0..3, <see cref="Chunk.CubeFaces"/> order) that has taken
    /// <paramref name="depthPx"/> of <see cref="WoodDamage.MaxDepthPx"/>: a slab perpendicular to the
    /// wood's run (<see cref="RunAxis"/>; a bite on a face the wood runs through is cut across Y instead),
    /// from the hit face in to the depth the damage reaches, as wide as the cell the other way.
    /// </summary>
    internal static BlockHitbox BiteBox(byte state, int biteFace, int depthPx)
    {
        var half = TreeWoodShape.HalfWidth(WoodState.Thickness(state));
        var width = 2f * half;
        var depth = Math.Clamp(depthPx / (float)WoodDamage.MaxDepthPx, 0f, 1f) * width * 0.9f;
        var band = MathF.Min(MathF.Max(MinBand, width * BandShare), width * 0.75f);

        // Faces 0/1 hit along Z, 2/3 along X; the slab is thin along the run and the bite goes in along the hit
        var hitAxis = biteFace < 2 ? 2 : 0;
        var run = RunAxis(state);
        if (run == hitAxis) run = 1;

        float[] min = [0f, 0f, 0f], max = [1f, 1f, 1f];
        min[run] = 0.5f - band * 0.5f;
        max[run] = 0.5f + band * 0.5f;
        if ((biteFace & 1) == 0) max[hitAxis] = 0.5f - half + depth;
        else min[hitAxis] = 0.5f + half - depth;
        return new BlockHitbox(new Vector3(min[0], min[1], min[2]), new Vector3(max[0], max[1], max[2]));
    }

    /// <summary>Appends what is left of <paramref name="box"/> once <paramref name="cut"/> is
    /// removed: up to six boxes that tile the remainder exactly without overlapping.</summary>
    internal static void Subtract(BlockHitbox box, BlockHitbox cut, List<BlockHitbox> into)
    {
        var lo = Vector3.Max(box.Min, cut.Min);
        var hi = Vector3.Min(box.Max, cut.Max);
        if (lo.X >= hi.X || lo.Y >= hi.Y || lo.Z >= hi.Z)
        {
            into.Add(box);
            return;
        }

        if (lo.X > box.Min.X) into.Add(new BlockHitbox(box.Min, box.Max with { X = lo.X }));
        if (hi.X < box.Max.X) into.Add(new BlockHitbox(box.Min with { X = hi.X }, box.Max));
        // Inside the cut's X range: below and above it, then in front of and behind it
        var xMin = lo.X;
        var xMax = hi.X;
        if (lo.Y > box.Min.Y)
            into.Add(new BlockHitbox(new Vector3(xMin, box.Min.Y, box.Min.Z), new Vector3(xMax, lo.Y, box.Max.Z)));
        if (hi.Y < box.Max.Y)
            into.Add(new BlockHitbox(new Vector3(xMin, hi.Y, box.Min.Z), new Vector3(xMax, box.Max.Y, box.Max.Z)));
        if (lo.Z > box.Min.Z)
            into.Add(new BlockHitbox(new Vector3(xMin, lo.Y, box.Min.Z), new Vector3(xMax, hi.Y, lo.Z)));
        if (hi.Z < box.Max.Z)
            into.Add(new BlockHitbox(new Vector3(xMin, lo.Y, hi.Z), new Vector3(xMax, hi.Y, box.Max.Z)));
    }

    /// <summary>
    /// Emits the thin wood cell at padded index <paramref name="pIdx"/> with its bite: the boxes of its
    /// shape minus <see cref="BiteBox"/>, every face of every piece (the pieces meet inside the wood
    /// where nothing sees them), the faces that look into the cut in the end-grain tile.
    /// </summary>
    internal static void Emit(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        ushort[] padded, byte[] paddedState, int pIdx, byte[] sunLight, byte[] blockLight,
        int x, int y, int z, BlockType type, Vector3 basePos, int biteFace, int depthPx)
    {
        var state = paddedState[pIdx];
        Span<ushort> nbIds = stackalloc ushort[6];
        Span<byte> nbStates = stackalloc byte[6];
        Span<FaceLight> lights = stackalloc FaceLight[6];
        var lightBase = LightIdx(x, y, z);
        for (var i = 0; i < 6; i++)
        {
            nbIds[i] = padded[pIdx + ChunkFaceTables.FaceNeighborDelta[i]];
            nbStates[i] = paddedState[pIdx + ChunkFaceTables.FaceNeighborDelta[i]];
            lights[i] = EmitterFaceLight(i, padded, pIdx, sunLight, blockLight, lightBase, lightBase);
        }

        var cut = BiteBox(state, biteFace, depthPx);
        var pieces = new List<BlockHitbox>();
        foreach (var box in TreeWoodShape.GetBoxes(type.Id, state, nbIds, nbStates))
            Subtract(box, cut, pieces);

        var half = TreeWoodShape.HalfWidth(WoodState.Thickness(state));
        foreach (var piece in pieces)
        {
            // Only the node's own ends show the cap tiles; a tube's top and bottom are bark like its sides
            var withinNode = piece.Min.X >= 0.5f - half - 1e-4f && piece.Max.X <= 0.5f + half + 1e-4f
                             && piece.Min.Z >= 0.5f - half - 1e-4f && piece.Max.Z <= 0.5f + half + 1e-4f;
            for (var face = 0; face < 6; face++)
            {
                var tile = FacesIntoCut(piece, cut, face) ? type.Top
                    : withinNode && face == 4 ? type.Top
                    : withinNode && face == 5 ? type.Bottom
                    : type.Side;
                ChunkMesher.EmitBoxFace(vertices, indices, ref indexOffset, basePos, piece.Min, piece.Max, face, tile,
                    ChunkFaceTables.ShadeLut[(face << 2) | 3], lights[face]);
            }
        }
    }

    // Whether the piece's face lies on a plane of the cut and looks toward it: the wall of the bite
    private static bool FacesIntoCut(BlockHitbox piece, BlockHitbox cut, int face)
    {
        const float eps = 1e-4f;
        return face switch
        {
            0 => MathF.Abs(piece.Min.Z - cut.Max.Z) < eps && Overlaps(piece, cut, 0, 1),
            1 => MathF.Abs(piece.Max.Z - cut.Min.Z) < eps && Overlaps(piece, cut, 0, 1),
            2 => MathF.Abs(piece.Min.X - cut.Max.X) < eps && Overlaps(piece, cut, 1, 2),
            3 => MathF.Abs(piece.Max.X - cut.Min.X) < eps && Overlaps(piece, cut, 1, 2),
            4 => MathF.Abs(piece.Max.Y - cut.Min.Y) < eps && Overlaps(piece, cut, 0, 2),
            _ => MathF.Abs(piece.Min.Y - cut.Max.Y) < eps && Overlaps(piece, cut, 0, 2),
        };
    }

    // Whether the two boxes' extents overlap on both of the given axes (0 = X, 1 = Y, 2 = Z)
    private static bool Overlaps(BlockHitbox a, BlockHitbox b, int axis1, int axis2) =>
        Overlap(a, b, axis1) && Overlap(a, b, axis2);

    private static bool Overlap(BlockHitbox a, BlockHitbox b, int axis) => axis switch
    {
        0 => a.Min.X < b.Max.X && b.Min.X < a.Max.X,
        1 => a.Min.Y < b.Max.Y && b.Min.Y < a.Max.Y,
        _ => a.Min.Z < b.Max.Z && b.Min.Z < a.Max.Z,
    };
}
