using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Data;
using VoxelEngine.WorldGen.Features;
using static VoxelEngine.Chunks.Chunk;
using static VoxelEngine.Chunks.Meshing.ChunkLighting;

namespace VoxelEngine.Chunks.Meshing;

/// <summary>
/// GL-free emitters of the stack blocks: pebble piles, crossed-stick branch piles and layered
/// snow, each lit by bilinear <see cref="FaceLight"/> samples of the cell faces it touches.
/// </summary>
internal static class StackEmitter
{
    /// <summary>
    /// Emits the 1-3 ankle-high stones sharing a pebble cell: floor-sitting cubes sized and
    /// spun per stone (see PebbleGenerator), with the stone tile cropped to their size so
    /// texel density matches full cubes. UVs come from the unrotated corners, so the texture
    /// stays put while the geometry spins. The bottom face is skipped: it sits coplanar on
    /// the ground's top face, which still draws because pebbles never occlude.
    /// </summary>
    internal static void EmitPebbles(int worldX0, int worldZ0, List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        ushort[] padded, byte[] paddedState, int pIdx, byte[] sunLight, byte[] blockLight,
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
                ChunkFaceTables.BoxFaceCorners(min, max, face, out var p0, out var p1, out var p2, out var p3);
                var normal = CubeFaces[face].normal;
                ChunkMesher.EmitQuad(vertices, indices, ref indexOffset, basePos,
                    SpinY(p0, cx, cz, cos, sin), SpinY(p1, cx, cz, cos, sin),
                    SpinY(p2, cx, cz, cos, sin), SpinY(p3, cx, cz, cos, sin),
                    SpinY(normal, 0f, 0f, cos, sin),
                    ChunkFaceTables.CropUv(face, p0), ChunkFaceTables.CropUv(face, p1), ChunkFaceTables.CropUv(face, p2), ChunkFaceTables.CropUv(face, p3), tile,
                    ChunkFaceTables.ShadeLut[(face << 2) | 3], lights[face]);
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
        ushort[] padded, byte[] paddedState, int pIdx, byte[] sunLight, byte[] blockLight,
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
            BranchStick(worldX, worldZ, variant, b, out var min, out var max, out var cx, out var cz, out var cos, out var sin);
            for (var face = 0; face < 6; face++)
            {
                if (face == 5) continue;
                var tile = face is 2 or 3 ? type.Top : type.Side;
                ChunkFaceTables.BoxFaceCorners(min, max, face, out var p0, out var p1, out var p2, out var p3);
                var normal = CubeFaces[face].normal;
                ChunkMesher.EmitQuad(vertices, indices, ref indexOffset, basePos,
                    SpinY(p0, cx, cz, cos, sin), SpinY(p1, cx, cz, cos, sin),
                    SpinY(p2, cx, cz, cos, sin), SpinY(p3, cx, cz, cos, sin),
                    SpinY(normal, 0f, 0f, cos, sin),
                    ChunkFaceTables.CropUv(face, p0), ChunkFaceTables.CropUv(face, p1), ChunkFaceTables.CropUv(face, p2), ChunkFaceTables.CropUv(face, p3), tile,
                    ChunkFaceTables.ShadeLut[(face << 2) | 3], lights[face]);
            }
        }
    }

    /// <summary>
    /// The box of stick <paramref name="b"/> of a branch pile at (worldX, worldZ), before its spin: the
    /// cell-local corners, the spin pivot (cx, cz) and the yaw's cosine and sine. Shared by the chunk
    /// emitter and the HUD's pile model, so the two draw the same sticks.
    /// </summary>
    internal static void BranchStick(int worldX, int worldZ, int variant, int b, out Vector3 min, out Vector3 max,
        out float cx, out float cz, out float cos, out float sin)
    {
        BranchPileGenerator.GetCenter(worldX, worldZ, variant, b, out cx, out cz);
        var half = BranchPileGenerator.LengthAt(worldX, worldZ, b) * 0.5f;
        var halfThick = BranchPileGenerator.Thickness * 0.5f;
        var yaw = BranchPileGenerator.YawAt(worldX, worldZ, b);
        cos = MathF.Cos(yaw);
        sin = MathF.Sin(yaw);
        var y0 = BranchPileGenerator.LayerY(b);
        min = new Vector3(cx - half, y0, cz - halfThick);
        max = new Vector3(cx + half, y0 + BranchPileGenerator.Thickness, cz + halfThick);
    }

    /// <summary>
    /// Emits a snow layer cell as one box from the floor up to its depth (see
    /// <see cref="SnowShape"/>): the state byte holds 1..StackMax layers and the box is that
    /// fraction of a block tall, so a full stack is a full cube. The bottom face is skipped unless
    /// the cell below cannot occlude, since a layer normally rests on ground that draws its own top.
    /// A side is skipped against an opaque cube or a snow cell at least as deep (a drift's inner
    /// walls), and the top of a full stack under an opaque cube or more snow (<see cref="SnowFaceHidden"/>).
    /// </summary>
    internal static void EmitSnowLayers(int worldX0, int worldY0, int worldZ0, List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        ushort[] padded, byte[] paddedState, int pIdx, byte[] sunLight, byte[] blockLight,
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

        var drawBottom = count >= maxCount && !BlockType.Occludes(padded[pIdx + ChunkFaceTables.FaceNeighborDelta[5]]);
        var min = new Vector3(0f, 0f, 0f);
        var max = new Vector3(1f, height, 1f);
        for (var face = 0; face < 6; face++)
        {
            if (face == 5 && !drawBottom) continue;
            if (face < 5)
            {
                var n = pIdx + ChunkFaceTables.FaceNeighborDelta[face];
                var normal = CubeFaces[face].normal;
                var neighborCount = padded[n] == type.Id
                    ? SnowShape.CountWithState(worldX + (int)normal.X, worldY0 + y + (int)normal.Y, worldZ + (int)normal.Z,
                        paddedState[n], maxCount)
                    : 0;
                if (SnowFaceHidden(face, count, maxCount, BlockType.Occludes(padded[n]), neighborCount)) continue;
            }
            var tile = face == 4 ? type.Top : face == 5 ? type.Bottom : type.Side;
            ChunkFaceTables.BoxFaceCorners(min, max, face, out var p0, out var p1, out var p2, out var p3);
            ChunkMesher.EmitQuad(vertices, indices, ref indexOffset, basePos, p0, p1, p2, p3,
                CubeFaces[face].normal,
                ChunkFaceTables.CropUv(face, p0), ChunkFaceTables.CropUv(face, p1), ChunkFaceTables.CropUv(face, p2), ChunkFaceTables.CropUv(face, p3), tile,
                ChunkFaceTables.ShadeLut[(face << 2) | 3], lights[face]);
        }
    }

    /// <summary>
    /// Whether face <paramref name="face"/> (0-3 sides, 4 top) of a snow cell <paramref name="count"/> layers deep is
    /// covered by its neighbor: an opaque cube covers any face; a snow neighbor <paramref name="neighborCount"/> layers
    /// deep (0 when it is not snow) covers a side no taller than itself, and covers the top of a full stack, since even
    /// one layer spans the whole floor of its cell. A top below a full stack is open whatever sits above it.
    /// </summary>
    internal static bool SnowFaceHidden(int face, int count, int maxCount, bool neighborOccludes, int neighborCount)
    {
        if (face == 4)
            return count >= maxCount && (neighborOccludes || neighborCount > 0);
        return neighborOccludes || neighborCount >= count;
    }

    /// <summary>Rotates a point about (cx, cz) by a Y spin; normals pass through with cx = cz = 0.</summary>
    internal static Vector3 SpinY(Vector3 p, float cx, float cz, float cos, float sin)
    {
        var dx = p.X - cx;
        var dz = p.Z - cz;
        return new Vector3(cx + dx * cos + dz * sin, p.Y, cz - dx * sin + dz * cos);
    }
}
