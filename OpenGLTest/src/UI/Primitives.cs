using System;
using System.Numerics;
using Silk.NET.OpenGL;

namespace silkgltriangle;

/// <summary>
/// Mesh factories for simple shapes, built in the same <see cref="Vertex"/> layout the world
/// meshes use so any of the existing shaders can draw them. Every shape is centred on the
/// origin and sized in local units, leaving placement to the caller's model matrix — a single
/// unit shape can therefore be built once and reused at any size.
///
/// Shapes carry a vertex colour with alpha. A solid-colour shape is just an untextured draw
/// (see uUseTexture in ui-fragment.glsl); a textured one takes a <see cref="UvRect"/>, either
/// the full image or one tile of the block atlas.
/// </summary>
public static class Primitives
{
    private const int FaceCount = 6;
    private const int CornersPerFace = 4;

    /// <summary>
    /// A square in the XY plane at z = 0, centred on the origin. Y grows upwards in the
    /// shape's own space; a UI projection with a flipped Y axis renders it upright.
    /// </summary>
    public static Mesh CreateSquare(GL gl, float size = 1f, Rgba? color = null, UvRect? uv = null)
        => CreateRectangle(gl, size, size, color, uv);

    /// <summary>A rectangle in the XY plane at z = 0, centred on the origin.</summary>
    public static Mesh CreateRectangle(GL gl, float width, float height, Rgba? color = null, UvRect? uv = null)
    {
        var tint = color ?? Rgba.White;
        var halfWidth = width * 0.5f;
        var halfHeight = height * 0.5f;

        Span<Vector2> corners = stackalloc Vector2[CornersPerFace];
        (uv ?? UvRect.Full).CornersTo(corners);

        Span<Vertex> vertices =
        [
            Vert(new Vector3(-halfWidth, -halfHeight, 0f), tint, corners[0]),
            Vert(new Vector3(-halfWidth, halfHeight, 0f), tint, corners[1]),
            Vert(new Vector3(halfWidth, halfHeight, 0f), tint, corners[2]),
            Vert(new Vector3(halfWidth, -halfHeight, 0f), tint, corners[3])
        ];

        Span<ushort> indices = [0, 1, 2, 0, 2, 3];
        return new Mesh(gl, vertices, indices);
    }

    /// <summary>A cube centred on the origin, with the same <see cref="UvRect"/> on all faces.</summary>
    public static Mesh CreateCube(GL gl, float size = 1f, Rgba? color = null, UvRect? uv = null)
    {
        Span<UvRect> faceUvs = stackalloc UvRect[FaceCount];
        faceUvs.Fill(uv ?? UvRect.Full);
        return CreateCube(gl, size, color, faceUvs);
    }

    /// <summary>
    /// A cube centred on the origin with a per-face texture region, indexed in
    /// <see cref="Chunk.CubeFaces"/> order: -Z, +Z, -X, +X, +Y, -Y.
    /// </summary>
    /// <param name="faceShades">
    /// Optional per-face brightness multiplier, so a cube reads as lit without normals in the
    /// vertex format. Defaults to flat, unshaded faces.
    /// </param>
    public static Mesh CreateCube(GL gl, float size, Rgba? color, ReadOnlySpan<UvRect> faceUvs,
        ReadOnlySpan<float> faceShades = default)
    {
        if (faceUvs.Length < FaceCount)
            throw new ArgumentException($"Expected {FaceCount} face UVs, got {faceUvs.Length}", nameof(faceUvs));

        var tint = color ?? Rgba.White;
        var vertices = new Vertex[FaceCount * CornersPerFace];
        var indices = new ushort[FaceCount * 6];
        Span<Vector2> corners = stackalloc Vector2[CornersPerFace];

        for (var face = 0; face < FaceCount; face++)
        {
            faceUvs[face].CornersTo(corners);

            var shaded = faceShades.IsEmpty ? tint : Shade(tint, faceShades[face]);
            // CubeFaces positions span the 0..1 unit cube, so recentre before scaling
            var positions = Chunk.CubeFaces[face].face;

            for (var corner = 0; corner < CornersPerFace; corner++)
            {
                var position = (positions[corner] - new Vector3(0.5f)) * size;
                vertices[face * CornersPerFace + corner] = Vert(position, shaded, corners[corner]);
            }

            var offset = (ushort)(face * CornersPerFace);
            indices[face * 6 + 0] = offset;
            indices[face * 6 + 1] = (ushort)(offset + 1);
            indices[face * 6 + 2] = (ushort)(offset + 2);
            indices[face * 6 + 3] = offset;
            indices[face * 6 + 4] = (ushort)(offset + 2);
            indices[face * 6 + 5] = (ushort)(offset + 3);
        }

        return new Mesh(gl, vertices, indices);
    }

    /// <summary>
    /// A cube for one block type, centred on the origin, wearing that type's atlas tiles and
    /// per-face brightness. Faces are shaded as fully unoccluded (AO 3) — used both for the
    /// mining pop, where the block has nothing beside it, and for inventory icons.
    /// </summary>
    public static Mesh CreateBlockCube(GL gl, BlockType type, float size = 1f)
    {
        Span<UvRect> faceUvs = stackalloc UvRect[FaceCount];
        Span<float> faceShades = stackalloc float[FaceCount];

        for (var face = 0; face < FaceCount; face++)
        {
            faceUvs[face] = UvRect.Tile(type.FaceTiles[face]);
            // ShadeLut already folds brightness and AO into a 0..255 byte
            faceShades[face] = Chunk.ShadeLut[(face << 2) | 3] / 255f;
        }

        return CreateCube(gl, size, Rgba.White, faceUvs, faceShades);
    }

    private static Vertex Vert(Vector3 position, Rgba color, Vector2 uv) =>
        new(position, color.R, color.G, color.B, color.A, uv);

    private static Rgba Shade(Rgba color, float factor) =>
        Rgba.FromFloat(color.R / 255f * factor, color.G / 255f * factor, color.B / 255f * factor,
            color.A / 255f);
}
