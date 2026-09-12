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
    private readonly Dictionary<byte, (Mesh Mesh, Dictionary<int, uint> Textures)> _entries = new();

    public ItemMeshCache(GL gl) => _gl = gl;

    /// <summary>
    /// Raised after this cache has uploaded a texture, i.e. after it has bound (and unbound) a
    /// Texture2DArray on whatever texture unit happened to be active. Textures are built lazily,
    /// on first sight of an item frame, which means this can fire in the middle of a render pass;
    /// <c>RenderManager</c> hooks it up to its texture-unit cache so that clobbering is recorded
    /// rather than merely survived by luck (PERF_TODO R5). Null by default - nothing here needs it.
    /// </summary>
    internal Action? GlStateChanged { get; set; }

    /// <summary>The mesh and texture for an item id, or false for non-item ids.</summary>
    public bool TryGet(byte itemId, out Mesh mesh, out uint texture) =>
        TryGetFrame(itemId, 0, out mesh, out texture);

    /// <summary>
    /// The extruded mesh plus the texture of one spritesheet frame, for items whose look
    /// changes over time (the held compass tracks its heading). Geometry is identical for
    /// every frame, so one mesh is shared and only the per-frame texture is cached.
    /// </summary>
    public bool TryGetFrame(byte itemId, int frame, out Mesh mesh, out uint texture)
    {
        mesh = null!;
        texture = 0;
        if (!ItemType.Types.TryGetValue(itemId, out var type))
            return false;

        frame = Math.Clamp(frame, 0, Math.Max(0, type.FrameCount - 1));
        if (!_entries.TryGetValue(itemId, out var entry))
        {
            entry = (BuildMesh(type, frame), new Dictionary<int, uint>());
            _entries[itemId] = entry;
        }

        mesh = entry.Mesh;
        if (!entry.Textures.TryGetValue(frame, out texture))
        {
            texture = BuildTexture(type, frame);
            entry.Textures[frame] = texture;
        }
        return true;
    }

    private Mesh BuildMesh(ItemType type, int frame)
    {
        var (pixels, width, height) = FramePixels(type, frame);
        return Primitives.CreateExtrudedSprite(_gl, pixels, width, height);
    }

    private uint BuildTexture(ItemType type, int frame)
    {
        var (pixels, width, height) = FramePixels(type, frame);
        var texture = TextureLoader.Upload(_gl, pixels, width, height);
        // Upload bound and unbound a Texture2DArray on the live active unit; tell whoever is
        // tracking unit state. Only the texture path needs this - BuildMesh touches no unit.
        GlStateChanged?.Invoke();
        return texture;
    }

    // One cell of a spritesheet, or the whole texture for a plain sprite. The mesh it
    // extrudes is identical for every frame, so callers share it and vary only the texture.
    private static (byte[] Pixels, int Width, int Height) FramePixels(ItemType type, int frame)
    {
        using var stream = File.OpenRead(type.TexturePath);
        var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);

        if (type.Animation == ItemAnimationMode.None)
            return (image.Data, image.Width, image.Height);

        if (image.Width % type.SpriteCols != 0 || image.Height % type.SpriteRows != 0)
            throw new InvalidOperationException(
                $"Item '{type.TexturePath}' is {image.Width}x{image.Height} but its spritesheet is {type.SpriteCols}x{type.SpriteRows}: cells must divide the texture evenly.");
        var cellWidth = image.Width / type.SpriteCols;
        var cellHeight = image.Height / type.SpriteRows;
        return (TextureLoader.CropFrame(image.Data, image.Width, image.Height,
            type.SpriteCols, type.SpriteRows, frame), cellWidth, cellHeight);
    }

    public void Dispose()
    {
        foreach (var (mesh, textures) in _entries.Values)
        {
            mesh.Dispose();
            foreach (var texture in textures.Values)
                _gl.DeleteTexture(texture);
        }
        _entries.Clear();

        GC.SuppressFinalize(this);
    }
}
