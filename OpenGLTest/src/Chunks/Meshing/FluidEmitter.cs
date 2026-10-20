using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Numerics;
using VoxelEngine.Data;
using VoxelEngine.Effects.Lighting;
using VoxelEngine.Simulation.Fluids;
using static VoxelEngine.Chunks.Chunk;
using static VoxelEngine.Chunks.Meshing.ChunkLighting;

namespace VoxelEngine.Chunks.Meshing;

/// <summary>
/// GL-free fluid mesh emission: a fluid cell's surface and side quads with order-independent
/// corner heights, corner light and per-corner flow codes, so neighboring fluid cells agree at
/// every shared corner.
/// </summary>
internal static class FluidEmitter
{
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
            var neighbor = padded[pIdx + ChunkFaceTables.FaceNeighborDelta[face]];

            // Two fluid cells share no face: their sloped tops already meet at the corners
            if (BlockType.IsFluid(neighbor)) continue;
            if (face != 4 && BlockType.Occludes(neighbor)) continue;
            // A full column pressed against a ceiling has a coplanar top face - drop it
            // instead of z-fighting the block above
            if (face == 4 && BlockType.Occludes(neighbor) && minCorner >= 0.999f) continue;

            // Meshes are indexed with ushort (mirrors LodFaceEmitter.AddQuad): stop emitting
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
            ChunkMesher.EmitQuad(vertices, indices, ref indexOffset, basePos, a, b, c, d, normal,
                ChunkFaceTables.CropUv(face, a), ChunkFaceTables.CropUv(face, b), ChunkFaceTables.CropUv(face, c), ChunkFaceTables.CropUv(face, d), tile,
                ChunkFaceTables.ShadeLut[(face << 2) | 3], light, 254);

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
    /// (<see cref="FluidFlow.Corner{T}"/> scaled by <see cref="WaterRenderConfig.WaterFlowFullStrength"/>,
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
                : MathF.Min(v.Length() / WaterRenderConfig.WaterFlowFullStrength, 1f);
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
        // blockLight holds level | slot << 4 (see ChunkLighting.ComputeLight)
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
        BlockType.IsFluid(padded[p + ChunkFaceTables.FaceNeighborDelta[4]])
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
                if (BlockType.IsFluid(padded[q + ChunkFaceTables.FaceNeighborDelta[4]])) return 1f;

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
}
