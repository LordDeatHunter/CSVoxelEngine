using System;
using System.Collections.Generic;
using System.IO;
using Silk.NET.OpenGL;
using StbImageSharp;

namespace silkgltriangle;

/// <summary>
/// Lazily built extruded-sprite meshes plus their GL textures, one per <see cref="ItemType"/>.
/// Mirrors <see cref="BlockCubeCache"/>: the world drop pass and (later) any other item draw
/// share one copy. Everything here is created on first sight from the main thread, where GL
/// calls are legal.
/// </summary>
public class ItemMeshCache : IDisposable
{
    private readonly GL _gl;
    private readonly Dictionary<byte, (Mesh Mesh, uint Texture)> _entries = new();

    public ItemMeshCache(GL gl) => _gl = gl;

    /// <summary>The mesh and texture for an item id, or false for non-item ids.</summary>
    public bool TryGet(byte itemId, out Mesh mesh, out uint texture)
    {
        if (_entries.TryGetValue(itemId, out var entry))
        {
            (mesh, texture) = entry;
            return true;
        }

        if (!ItemType.Types.TryGetValue(itemId, out var type))
        {
            (mesh, texture) = (null!, 0);
            return false;
        }

        entry = Build(type);
        _entries[itemId] = entry;
        (mesh, texture) = entry;
        return true;
    }

    private (Mesh Mesh, uint Texture) Build(ItemType type)
    {
        using var stream = File.OpenRead(type.TexturePath);
        var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);

        var texture = TextureLoader.Upload(_gl, image.Data, image.Width, image.Height);
        var mesh = Primitives.CreateExtrudedSprite(_gl, image.Data, image.Width, image.Height);
        return (mesh, texture);
    }

    public void Dispose()
    {
        foreach (var (mesh, texture) in _entries.Values)
        {
            mesh.Dispose();
            _gl.DeleteTexture(texture);
        }
        _entries.Clear();

        GC.SuppressFinalize(this);
    }
}
