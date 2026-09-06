using System;
using System.Collections.Generic;
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
            var normal = Chunk.CubeFaces[face].normal;

            for (var corner = 0; corner < CornersPerFace; corner++)
            {
                var position = (positions[corner] - new Vector3(0.5f)) * size;
                vertices[face * CornersPerFace + corner] = Vert(position, shaded, corners[corner], normal);
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

    /// <summary>
    /// A 2D sprite extruded by two texels: full-texture front and back faces plus side walls
    /// around every opaque pixel's exposed edges, so the sprite reads as a thin slab with the
    /// texture's own silhouette. Transparent fragments are cut out by the world shader's
    /// alpha discard, exactly like leaf holes. <paramref name="rgba"/> holds top-first rows
    /// (StbImageSharp order); a pixel is solid when its alpha is at least 128.
    /// </summary>
    public static Mesh CreateExtrudedSprite(GL gl, byte[] rgba, int width, int height, float size = 1f)
    {
        var half = size * 0.5f;
        var depth = size / width * 2f;
        var zFront = depth * 0.5f;
        var zBack = -zFront;

        bool Solid(int x, int y)
        {
            if (x < 0 || y < 0 || x >= width || y >= height) return false;
            return rgba[(y * width + x) * 4 + 3] >= 128;
        }

        var vertices = new List<Vertex>(64);
        var indices = new List<ushort>(96);

        // Front (+Z) and back (-Z) faces: the whole sprite, upright (image top maps to +Y).
        // UV v = 0 is the image's top row: TextureLoader uploads rows top-first with no flip,
        // so the first memory row lands on v = 0.
        Span<Vector2> corners = stackalloc Vector2[CornersPerFace];
        UvRect.Full.CornersTo(corners);
        Span<Vector3> facePositions =
        [
            new Vector3(-half, -half, zFront),
            new Vector3(-half, half, zFront),
            new Vector3(half, half, zFront),
            new Vector3(half, -half, zFront),
        ];
        for (var i = 0; i < CornersPerFace; i++)
            vertices.Add(Vert(facePositions[i], Rgba.White, corners[i], Vector3.UnitZ));
        indices.AddRange([0, 1, 2, 0, 2, 3]);

        var backBase = (ushort)vertices.Count;
        for (var i = 0; i < CornersPerFace; i++)
            vertices.Add(Vert(facePositions[i] with { Z = zBack }, Rgba.White, corners[i], -Vector3.UnitZ));
        indices.AddRange([(ushort)(backBase + 0), (ushort)(backBase + 2), (ushort)(backBase + 1),
            (ushort)(backBase + 0), (ushort)(backBase + 3), (ushort)(backBase + 2)]);

        // Side walls, one quad per exposed pixel edge, UV'd to the edge texel's centre so
        // nearest filtering paints the wall the texel's own colour.
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (!Solid(x, y)) continue;

            var x0 = x / (float)width * size - half;
            var x1 = (x + 1) / (float)width * size - half;
            var y1 = half - y / (float)height * size;
            var y0 = half - (y + 1) / (float)height * size;
            var uv = new Vector2((x + 0.5f) / width, (y + 0.5f) / height);

            if (!Solid(x, y - 1)) AddWall(new Vector3(x0, y1, zFront), new Vector3(x1, y1, zFront),
                new Vector3(x1, y1, zBack), new Vector3(x0, y1, zBack), Vector3.UnitY, uv);
            if (!Solid(x, y + 1)) AddWall(new Vector3(x0, y0, zFront), new Vector3(x0, y0, zBack),
                new Vector3(x1, y0, zBack), new Vector3(x1, y0, zFront), -Vector3.UnitY, uv);
            if (!Solid(x - 1, y)) AddWall(new Vector3(x0, y0, zFront), new Vector3(x0, y1, zFront),
                new Vector3(x0, y1, zBack), new Vector3(x0, y0, zBack), -Vector3.UnitX, uv);
            if (!Solid(x + 1, y)) AddWall(new Vector3(x1, y0, zFront), new Vector3(x1, y0, zBack),
                new Vector3(x1, y1, zBack), new Vector3(x1, y1, zFront), Vector3.UnitX, uv);
        }

        void AddWall(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Vector2 wallUv)
        {
            var quadBase = (ushort)vertices.Count;
            vertices.Add(Vert(a, Rgba.White, wallUv, normal));
            vertices.Add(Vert(b, Rgba.White, wallUv, normal));
            vertices.Add(Vert(c, Rgba.White, wallUv, normal));
            vertices.Add(Vert(d, Rgba.White, wallUv, normal));
            indices.AddRange([(ushort)(quadBase + 0), (ushort)(quadBase + 1), (ushort)(quadBase + 2),
                (ushort)(quadBase + 0), (ushort)(quadBase + 2), (ushort)(quadBase + 3)]);
        }

        return new Mesh(gl,
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(vertices),
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(indices));

    }

    private static Vertex Vert(Vector3 position, Rgba color, Vector2 uv, Vector3? normal = null) =>
        new(position, color.R, color.G, color.B, color.A, uv, 1f, 0f, normal);

    private static Rgba Shade(Rgba color, float factor) =>
        Rgba.FromFloat(color.R / 255f * factor, color.G / 255f * factor, color.B / 255f * factor,
            color.A / 255f);
}
