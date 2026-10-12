using System;
using System.Numerics;

namespace VoxelEngine.Interaction;

/// <summary>
/// GL-free geometry of the block-selection outline: the twelve edges of a box, each drawn as a
/// thin axis-aligned beam (a box of its own) instead of a GL line. <c>glLineWidth</c> above 1 is
/// implementation-defined in a core profile and is often clamped to 1 px, so the old line outline
/// rendered at whatever width the driver felt like; triangles are drawn the same everywhere.
/// Each beam spans its edge grown by half the thickness at both ends, so the beams overlap at
/// the corners and the corners come out filled rather than notched.
/// </summary>
public static class SelectionOutlineMesh
{
    private const int BeamCount = 12;
    private const int CornersPerBeam = 8;
    private const int IndicesPerBeam = 36;

    /// <summary>Vertices written by <see cref="BuildVertices"/>: 12 beams of 8 corners.</summary>
    public const int VertexCount = BeamCount * CornersPerBeam;

    /// <summary>Indices written by <see cref="BuildIndices"/>: 12 beams of 12 triangles.</summary>
    public const int IndexCount = BeamCount * IndicesPerBeam;

    // A beam's corners are numbered bit0 = x, bit1 = y, bit2 = z (0 = min, 1 = max). Each face is
    // listed counter-clockwise seen from outside, so every triangle faces outward.
    private static readonly int[] BeamFaces =
    [
        0, 4, 6, 2, // -X
        1, 3, 7, 5, // +X
        0, 1, 5, 4, // -Y
        2, 6, 7, 3, // +Y
        0, 2, 3, 1, // -Z
        4, 5, 7, 6  // +Z
    ];

    /// <summary>Writes the <see cref="IndexCount"/> triangle indices; they never change.</summary>
    public static void BuildIndices(Span<uint> indices)
    {
        if (indices.Length < IndexCount)
            throw new ArgumentException($"need room for {IndexCount} indices", nameof(indices));

        var n = 0;
        for (var beam = 0; beam < BeamCount; beam++)
        {
            var baseVertex = (uint)(beam * CornersPerBeam);
            for (var f = 0; f < 6; f++)
            {
                uint a = (uint)BeamFaces[f * 4], b = (uint)BeamFaces[f * 4 + 1],
                    c = (uint)BeamFaces[f * 4 + 2], d = (uint)BeamFaces[f * 4 + 3];
                indices[n++] = baseVertex + a;
                indices[n++] = baseVertex + b;
                indices[n++] = baseVertex + c;
                indices[n++] = baseVertex + a;
                indices[n++] = baseVertex + c;
                indices[n++] = baseVertex + d;
            }
        }
    }

    /// <summary>
    /// Writes the <see cref="VertexCount"/> world-space corners of the twelve edge beams of the
    /// box <paramref name="min"/>..<paramref name="max"/>, each beam
    /// <paramref name="thickness"/> across and centred on its edge.
    /// </summary>
    public static void BuildVertices(Vector3 min, Vector3 max, float thickness, Span<Vector3> vertices)
    {
        if (vertices.Length < VertexCount)
            throw new ArgumentException($"need room for {VertexCount} vertices", nameof(vertices));

        var half = new Vector3(thickness * 0.5f);
        var n = 0;
        // axis = the direction the edge runs along; (u, v) pick which of the four parallel edges.
        for (var axis = 0; axis < 3; axis++)
        for (var u = 0; u < 2; u++)
        for (var v = 0; v < 2; v++)
        {
            Vector3 c0, c1;
            switch (axis)
            {
                case 0:
                    c0 = new Vector3(min.X, u == 0 ? min.Y : max.Y, v == 0 ? min.Z : max.Z);
                    c1 = c0 with { X = max.X };
                    break;
                case 1:
                    c0 = new Vector3(u == 0 ? min.X : max.X, min.Y, v == 0 ? min.Z : max.Z);
                    c1 = c0 with { Y = max.Y };
                    break;
                default:
                    c0 = new Vector3(u == 0 ? min.X : max.X, v == 0 ? min.Y : max.Y, min.Z);
                    c1 = c0 with { Z = max.Z };
                    break;
            }

            var lo = Vector3.Min(c0, c1) - half;
            var hi = Vector3.Max(c0, c1) + half;
            for (var corner = 0; corner < CornersPerBeam; corner++)
            {
                vertices[n++] = new Vector3(
                    (corner & 1) == 0 ? lo.X : hi.X,
                    (corner & 2) == 0 ? lo.Y : hi.Y,
                    (corner & 4) == 0 ? lo.Z : hi.Z);
            }
        }
    }
}
