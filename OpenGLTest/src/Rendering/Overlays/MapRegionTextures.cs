using System;
using System.Collections.Generic;
using Silk.NET.OpenGL;
using VoxelEngine.Map;

namespace VoxelEngine.Rendering.Overlays;

/// <summary>
/// The map's GL textures: one per region of <see cref="RegionChunks"/> x <see cref="RegionChunks"/> chunk columns of
/// the current layer, a single-slice RGBA8 array texture (the ui shader samples arrays) of one texel per block with
/// nearest filtering and no mips. A region's texture is created the first time a map draws it, filled from every tile
/// the <see cref="MapTracker"/> holds there; afterwards each rescanned tile is copied in by
/// <see cref="Sync"/>. A region with no tile has no texture and is remembered as empty until a tile lands in it. A
/// layer switch drops them all; past <see cref="MaxRegions"/> the least recently drawn go first. Render thread only.
/// </summary>
internal sealed class MapRegionTextures(GL gl) : IDisposable
{
    private const int RegionShift = 3;
    public const int RegionChunks = 1 << RegionShift;
    public const int RegionBlocks = RegionChunks * MapTile.Size;
    private const int MaxRegions = 384;

    private sealed class Region
    {
        public uint Texture;
        public long LastDrawn;
    }

    private readonly Dictionary<(int X, int Z), Region> _regions = new();
    private readonly HashSet<(int X, int Z)> _empty = [];
    private readonly List<(int X, int Z)> _evict = [];
    private static readonly byte[] Clear = new byte[RegionBlocks * RegionBlocks * 4];
    private MapTracker? _tracker;
    private MapLayer? _layer;
    private long _frame;

    /// <summary>The region holding chunk column (chunkX, chunkZ).</summary>
    public static (int X, int Z) RegionOf(int chunkX, int chunkZ) =>
        (chunkX >> RegionShift, chunkZ >> RegionShift);

    /// <summary>Takes the frame's rescanned tiles (call once a frame, after <see cref="MapTracker.Update"/>). A new
    /// tracker (another world) or a new layer drops every texture.</summary>
    public void Sync(MapTracker tracker)
    {
        _frame++;
        if (!ReferenceEquals(_tracker, tracker) || _layer != tracker.Layer)
        {
            DeleteAll();
            _tracker = tracker;
            _layer = tracker.Layer;
        }
        foreach (var tile in tracker.Changed)
        {
            var key = RegionOf(tile.ChunkX, tile.ChunkZ);
            _empty.Remove(key);
            if (_regions.TryGetValue(key, out var region))
                UploadTile(region.Texture, tile);
        }
    }

    /// <summary>The texture of region (regionX, regionZ), created and filled on first use; 0 when the tracker holds
    /// no tile there.</summary>
    public uint TextureFor(int regionX, int regionZ, MapTracker tracker)
    {
        var key = (regionX, regionZ);
        if (_regions.TryGetValue(key, out var region))
        {
            region.LastDrawn = _frame;
            return region.Texture;
        }
        if (_empty.Contains(key)) return 0;

        uint texture = 0;
        for (var dz = 0; dz < RegionChunks; dz++)
        for (var dx = 0; dx < RegionChunks; dx++)
        {
            if (!tracker.TryGetTile(regionX * RegionChunks + dx, regionZ * RegionChunks + dz, out var tile)) continue;
            if (texture == 0) texture = Create();
            UploadTile(texture, tile);
        }
        if (texture == 0)
        {
            _empty.Add(key);
            return 0;
        }
        if (_regions.Count >= MaxRegions) EvictOldest();
        _regions[key] = new Region { Texture = texture, LastDrawn = _frame };
        return texture;
    }

    private uint Create()
    {
        var texture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2DArray, texture);
        unsafe
        {
            fixed (byte* pixels = Clear)
                gl.TexImage3D(TextureTarget.Texture2DArray, 0, (int)InternalFormat.Rgba8, RegionBlocks, RegionBlocks, 1u, 0,
                    PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        }
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMaxLevel, 0);
        gl.BindTexture(TextureTarget.Texture2DArray, 0);
        return texture;
    }

    private void UploadTile(uint texture, MapTile tile)
    {
        var x = (tile.ChunkX & (RegionChunks - 1)) * MapTile.Size;
        var y = (tile.ChunkZ & (RegionChunks - 1)) * MapTile.Size;
        gl.BindTexture(TextureTarget.Texture2DArray, texture);
        unsafe
        {
            // Each texel packs R in its low byte, so the uints are the RGBA bytes in order on a little-endian CPU
            fixed (uint* pixels = tile.Texels)
                gl.TexSubImage3D(TextureTarget.Texture2DArray, 0, x, y, 0, MapTile.Size, MapTile.Size, 1u,
                    PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        }
        gl.BindTexture(TextureTarget.Texture2DArray, 0);
    }

    private void EvictOldest()
    {
        _evict.Clear();
        foreach (var (key, region) in _regions)
            if (region.LastDrawn < _frame) _evict.Add(key);
        _evict.Sort((a, b) => _regions[a].LastDrawn.CompareTo(_regions[b].LastDrawn));
        var drop = Math.Max(1, _evict.Count / 4);
        for (var i = 0; i < drop && i < _evict.Count; i++)
        {
            gl.DeleteTexture(_regions[_evict[i]].Texture);
            _regions.Remove(_evict[i]);
        }
    }

    private void DeleteAll()
    {
        foreach (var region in _regions.Values)
            gl.DeleteTexture(region.Texture);
        _regions.Clear();
        _empty.Clear();
    }

    public void Dispose() => DeleteAll();
}
