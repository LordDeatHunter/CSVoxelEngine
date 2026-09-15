using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace silkgltriangle;

/// <summary>
/// A runtime entity model: the cubes parsed from a Blockbench project, baked into one
/// <see cref="Mesh"/> in model space (each part's pivot added back) plus the skin pixels. A
/// single model matrix places the whole entity, so there is no per-part posing — entity motion
/// and squish are expressed by the caller's transform instead.
/// </summary>
public sealed class EntityModel
{
    private readonly List<(Vector3 Min, Vector3 Max, UvRect?[] Faces)> _cubes = [];

    private EntityModel(byte[] pixels, int width, int height)
    {
        TexturePixels = pixels;
        TextureWidth = width;
        TextureHeight = height;
    }

    /// <summary>Top-first RGBA skin, as the Blockbench project supplied it.</summary>
    public byte[] TexturePixels { get; }

    public int TextureWidth { get; }

    public int TextureHeight { get; }

    /// <summary>
    /// Loads a model and its skin. Entity models carry no required groups, so whatever the
    /// project authored is kept; a missing texture is a content error that names the file.
    /// </summary>
    public static EntityModel Load(string path)
    {
        var data = BbmodelLoader.Load(path, Array.Empty<string>());
        if (data.TexturePixels == null)
            throw new InvalidOperationException(
                $"Entity model '{path}' has no texture; add a 'textures' entry pointing at its skin.");

        var model = new EntityModel(data.TexturePixels, data.TextureWidth, data.TextureHeight);
        foreach (var part in data.Parts.Values)
            foreach (var cube in part.Cubes)
                model._cubes.Add((cube.Min + part.Pivot, cube.Max + part.Pivot, cube.Faces));
        return model;
    }

    /// <summary>Builds the one mesh a draw needs. The caller owns and disposes it.</summary>
    public Mesh BuildMesh(GL gl)
    {
        var vertices = new List<Vertex>();
        var indices = new List<ushort>();
        Span<Vector2> corners = stackalloc Vector2[4];

        foreach (var (min, max, faces) in _cubes)
        {
            var size = max - min;
            for (var face = 0; face < 6; face++)
            {
                if (faces[face] is not { } rect) continue;
                rect.CornersTo(corners);

                var positions = Chunk.CubeFaces[face].face;
                var normal = Chunk.CubeFaces[face].normal;
                var quadBase = (ushort)vertices.Count;
                for (var corner = 0; corner < 4; corner++)
                {
                    var p = positions[corner];
                    vertices.Add(new Vertex(
                        min + p.Scale(size),
                        255, 255, 255, 255, corners[corner], 0, 1f, 0f, normal));
                }
                indices.AddRange([(ushort)quadBase, (ushort)(quadBase + 1), (ushort)(quadBase + 2),
                    quadBase, (ushort)(quadBase + 2), (ushort)(quadBase + 3)]);
            }
        }

        return new Mesh(gl,
            CollectionsMarshal.AsSpan(vertices),
            CollectionsMarshal.AsSpan(indices));
    }
}
