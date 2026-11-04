using System.Numerics;
using System.Runtime.CompilerServices;
using VoxelEngine.Content.Models;
using VoxelEngine.Data;
using static VoxelEngine.Chunks.Chunk;

namespace VoxelEngine.Chunks.Meshing;

/// <summary>The mesher's per-face lookup tables (padded-snapshot neighbor deltas, AO sample deltas, the shade LUT) and the per-face corner and UV helpers every chunk, LOD, pop and icon mesh shares. Filled once from <see cref="Chunk.CubeFaces"/> and <see cref="AONeighbors"/>.</summary>
internal static class ChunkFaceTables
{
    internal static readonly int[] FaceNeighborDelta = new int[6];
    internal static readonly int[][] AODeltas = new int[6][]; // 12 per face: (side1, side2, corner) x 4 corners
    internal const float SmoothLightDivisor = 60f; // 4 samples x level 15
    internal static readonly float[] FaceBrightness = [0.85f, 0.95f, 0.8f, 0.9f, 1.0f, 0.75f]; // matches CubeFaces order
    internal static readonly byte[] ShadeLut = new byte[6 * 4]; // [face * 4 + ao]

    internal static readonly Vector3[][][] AONeighbors =
    [
        [
            [Vector3Directions.LeftBackward, Vector3Directions.DownBackward, Vector3Directions.DownLeftBackward],
            [Vector3Directions.LeftBackward, Vector3Directions.UpBackward, Vector3Directions.UpLeftBackward],
            [Vector3Directions.RightBackward, Vector3Directions.UpBackward, Vector3Directions.UpRightBackward],
            [Vector3Directions.RightBackward, Vector3Directions.DownBackward, Vector3Directions.DownRightBackward],
        ],
        [
            [Vector3Directions.RightForward, Vector3Directions.DownForward, Vector3Directions.DownRightForward],
            [Vector3Directions.RightForward, Vector3Directions.UpForward, Vector3Directions.UpRightForward],
            [Vector3Directions.LeftForward, Vector3Directions.UpForward, Vector3Directions.UpLeftForward],
            [Vector3Directions.LeftForward, Vector3Directions.DownForward, Vector3Directions.DownLeftForward],
        ],
        [
            [Vector3Directions.DownLeft, Vector3Directions.LeftForward, Vector3Directions.DownLeftForward],
            [Vector3Directions.UpLeft, Vector3Directions.LeftForward, Vector3Directions.UpLeftForward],
            [Vector3Directions.UpLeft, Vector3Directions.LeftBackward, Vector3Directions.UpLeftBackward],
            [Vector3Directions.DownLeft, Vector3Directions.LeftBackward, Vector3Directions.DownLeftBackward],
        ],
        [
            [Vector3Directions.DownRight, Vector3Directions.RightBackward, Vector3Directions.DownRightBackward],
            [Vector3Directions.UpRight, Vector3Directions.RightBackward, Vector3Directions.UpRightBackward],
            [Vector3Directions.UpRight, Vector3Directions.RightForward, Vector3Directions.UpRightForward],
            [Vector3Directions.DownRight, Vector3Directions.RightForward, Vector3Directions.DownRightForward],
        ],
        [
            [Vector3Directions.UpLeft, Vector3Directions.UpBackward, Vector3Directions.UpLeftBackward],
            [Vector3Directions.UpLeft, Vector3Directions.UpForward, Vector3Directions.UpLeftForward],
            [Vector3Directions.UpRight, Vector3Directions.UpForward, Vector3Directions.UpRightForward],
            [Vector3Directions.UpRight, Vector3Directions.UpBackward, Vector3Directions.UpRightBackward],
        ],
        [
            [Vector3Directions.DownLeft, Vector3Directions.DownForward, Vector3Directions.DownLeftForward],
            [Vector3Directions.DownLeft, Vector3Directions.DownBackward, Vector3Directions.DownLeftBackward],
            [Vector3Directions.DownRight, Vector3Directions.DownBackward, Vector3Directions.DownRightBackward],
            [Vector3Directions.DownRight, Vector3Directions.DownForward, Vector3Directions.DownRightForward],
        ]
    ];

    static ChunkFaceTables()
    {
        for (var i = 0; i < 6; i++)
        {
            FaceNeighborDelta[i] = PaddedDelta(CubeFaces[i].normal);

            var deltas = new int[12];
            for (var j = 0; j < 4; j++)
            for (var k = 0; k < 3; k++)
                deltas[j * 3 + k] = PaddedDelta(AONeighbors[i][j][k]);
            AODeltas[i] = deltas;

            for (var ao = 0; ao <= 3; ao++)
                ShadeLut[(i << 2) | ao] = (byte)(FaceBrightness[i] * ao * MathUtils.ONE_THIRD * 255);
        }
    }

    private static int PaddedDelta(Vector3 offset) =>
        (int)offset.X * PaddedSq + (int)offset.Y * Padded + (int)offset.Z;

    /// <summary>Corners of one axis-aligned face of the [min, max] box (cell-local).</summary>
    internal static void BoxFaceCorners(Vector3 min, Vector3 max, int face,
        out Vector3 a, out Vector3 b, out Vector3 c, out Vector3 d)
    {
        (a, b, c, d) = face switch
        {
            0 => (new Vector3(min.X, min.Y, min.Z), new Vector3(min.X, max.Y, min.Z),
                  new Vector3(max.X, max.Y, min.Z), new Vector3(max.X, min.Y, min.Z)),
            1 => (new Vector3(max.X, min.Y, max.Z), new Vector3(max.X, max.Y, max.Z),
                  new Vector3(min.X, max.Y, max.Z), new Vector3(min.X, min.Y, max.Z)),
            2 => (new Vector3(min.X, min.Y, max.Z), new Vector3(min.X, max.Y, max.Z),
                  new Vector3(min.X, max.Y, min.Z), new Vector3(min.X, min.Y, min.Z)),
            3 => (new Vector3(max.X, min.Y, min.Z), new Vector3(max.X, max.Y, min.Z),
                  new Vector3(max.X, max.Y, max.Z), new Vector3(max.X, min.Y, max.Z)),
            4 => (new Vector3(min.X, max.Y, min.Z), new Vector3(min.X, max.Y, max.Z),
                  new Vector3(max.X, max.Y, max.Z), new Vector3(max.X, max.Y, min.Z)),
            _ => (new Vector3(min.X, min.Y, max.Z), new Vector3(min.X, min.Y, min.Z),
                  new Vector3(max.X, min.Y, min.Z), new Vector3(max.X, min.Y, max.Z)),
        };
    }

    /// <summary>
    /// Tile-local UV of a point on an axis-aligned quad, in tile units. This is the one
    /// definition of how each face orients its texture: a full cube face maps its 0..1 cell to
    /// 0..1 on the tile, a sub-cell box (<see cref="CropUv"/>) samples the matching crop at the
    /// same texel density, and a quad merged over <paramref name="extent"/> cells runs 0..W and
    /// 0..H so the sampler's REPEAT tiles it. Per face, U runs along X or Z and V along Y (Z on
    /// top and bottom), mirrored where Minecraft's convention runs the other way — the same
    /// mirror <see cref="ModelBaker"/> applies, so baked cubes, procedural boxes and merged quads
    /// all sample identically and bark continues across log/branch joints at one scale.
    /// </summary>
    /// <param name="face">Engine face index (<see cref="Chunk.CubeFaces"/> order).</param>
    /// <param name="local">Point on the quad, relative to the quad's minimum corner.</param>
    /// <param name="extent">Size of the quad along each axis (1 on its plane axis).</param>
    internal static Vector2 TiledUv(int face, Vector3 local, Vector3 extent)
    {
        float u = face switch
        {
            0 => local.X,
            1 => extent.X - local.X,
            2 => extent.Z - local.Z,
            3 => local.Z,
            _ => local.X, // top and bottom run along X
        };
        float v = face switch
        {
            4 => extent.Z - local.Z, // top runs along Z
            5 => local.Z,            // bottom runs along Z
            _ => extent.Y - local.Y, // sides run bottom-to-top
        };
        return new Vector2(u, v);
    }

    /// <summary>Tile-local UV under a cell-local point of a sub-cell box (see <see cref="TiledUv"/>).</summary>
    internal static Vector2 CropUv(int face, Vector3 local) => TiledUv(face, local, Vector3.One);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int VertexAO(bool side1, bool side2, bool corner)
    {
        if (side1 && side2) return 0;
        return 3 - ((side1 ? 1 : 0) + (side2 ? 1 : 0) + (corner ? 1 : 0));
    }
}
