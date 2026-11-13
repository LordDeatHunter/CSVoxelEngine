using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Map;

/// <summary>
/// The map's tiles for one client world. It keeps a <see cref="MapTile"/> per (layer, chunk column) and rescans the
/// stale ones a few a frame, nearest first:
/// <list type="bullet">
/// <item>a column comes due when a chunk in it is meshed (<see cref="MarkColumnDirty"/>, hooked to
/// <see cref="ChunkManager.ChunkMeshed"/>): every block edit ends in a remesh, so a placed or broken block, a blast,
/// flowing fluid or a server's mirrored edit all reach the map with no hook of their own;</item>
/// <item>and when the player enters a new chunk column or the layer changes, every column within
/// <see cref="MapConfig.ScanRadiusChunks"/> that has no tile for the layer yet.</item>
/// </list>
/// Surface and sky tiles stay for the session (the explored map, capped at <see cref="MapConfig.MaxTiles"/>, the
/// farthest dropped first) and are what a world saves (<see cref="ExportExplored"/>, <see cref="Import"/>,
/// <c>MapSaveCodec</c>); a cave slice's tiles are dropped when the map leaves it. A scan that found nothing (none of the
/// column's chunks loaded) never replaces a tile. The first update rescans every column in range, saved tile or not, so a
/// map saved before a crash catches up with the chunks. GL-free; main thread only.
/// </summary>
public sealed class MapTracker(IMapCells cells)
{
    private readonly MapLayerSelector _selector = new();
    private readonly Dictionary<(MapLayer Layer, int X, int Z), MapTile> _tiles = new();
    private readonly HashSet<(int X, int Z)> _dirty = [];
    private readonly List<(int X, int Z, int DistanceSq)> _pick = [];
    private readonly List<(MapLayer Layer, int X, int Z)> _pickKeys = [];
    private readonly List<MapTile> _changed = [];
    private MapTile? _spare;
    private (int X, int Z)? _focus;

    /// <summary>The layer the map shows.</summary>
    public MapLayer Layer => _selector.Current;

    /// <summary>Whether the last <see cref="Update"/> switched the layer: every tile on screen is new.</summary>
    public bool LayerChanged { get; private set; }

    /// <summary>The tiles the last <see cref="Update"/> rescanned, for the renderer to upload.</summary>
    public IReadOnlyList<MapTile> Changed => _changed;

    /// <summary>How many tiles are kept, over every layer.</summary>
    public int TileCount => _tiles.Count;

    /// <summary>Moves whenever the explored map (the surface and sky tiles) changes, so a save writes it only then.</summary>
    public int ExploredVersion { get; private set; }

    // Whether a layer's tiles are the explored map, kept for the session and saved; a cave slice's are neither
    private static bool IsExplored(MapLayer layer) => layer.Kind != MapLayerKind.Cave;

    /// <summary>A copy of every surface and sky tile, for the save (which may encode it off the main thread).</summary>
    public List<(MapLayerKind Kind, MapTile Tile)> ExportExplored()
    {
        var tiles = new List<(MapLayerKind, MapTile)>();
        foreach (var ((layer, _, _), tile) in _tiles)
            if (IsExplored(layer)) tiles.Add((layer.Kind, tile.Clone()));
        return tiles;
    }

    /// <summary>Adds saved surface and sky tiles (a world opening), over whatever was scanned there; the first update
    /// still rescans the ones whose chunks are loaded.</summary>
    public void Import(IEnumerable<(MapLayerKind Kind, MapTile Tile)> tiles)
    {
        foreach (var (kind, tile) in tiles)
        {
            var layer = kind == MapLayerKind.Sky ? MapLayer.Sky : MapLayer.Surface;
            _tiles[(layer, tile.ChunkX, tile.ChunkZ)] = tile;
        }
        ExploredVersion++;
    }

    /// <summary>The current layer's tile of chunk column (chunkX, chunkZ), when it has been scanned.</summary>
    public bool TryGetTile(int chunkX, int chunkZ, out MapTile tile) =>
        _tiles.TryGetValue((Layer, chunkX, chunkZ), out tile!);

    /// <summary>Chunk column (chunkX, chunkZ) changed (a chunk in it was meshed): rescan it when it is near.</summary>
    public void MarkColumnDirty(int chunkX, int chunkZ) => _dirty.Add((chunkX, chunkZ));

    /// <summary>
    /// The frame's step for an eye at <paramref name="eye"/>: picks the layer (with its switch hold), queues the
    /// columns around a newly entered chunk column and rescans up to <see cref="MapConfig.TilesPerFrame"/> of the due
    /// ones, nearest first. <see cref="Changed"/> lists them afterwards.
    /// </summary>
    public void Update(Vector3 eye, float deltaTime)
    {
        _changed.Clear();
        var previous = Layer;
        LayerChanged = _selector.Update(MapLayer.For(eye.Y, IsRoofed(cells, eye)), deltaTime);
        if (LayerChanged && previous.Kind == MapLayerKind.Cave)
            DropLayer(previous);

        var focus = ((int)MathF.Floor(eye.X) >> Chunk.SizeShift, (int)MathF.Floor(eye.Z) >> Chunk.SizeShift);
        if (LayerChanged || _focus != focus)
        {
            // The first update queues every column in range, a saved tile too: its chunks may have changed since
            QueueAround(focus.Item1, focus.Item2, missingOnly: _focus != null);
            _focus = focus;
        }
        ScanDue(focus.Item1, focus.Item2);
        if (_tiles.Count > MapConfig.MaxTiles)
            EvictFarthest(focus.Item1, focus.Item2);
    }

    /// <summary>
    /// Whether an eye at <paramref name="eye"/> stands under a roof: an occluding block (a full opaque cube, so
    /// never leaves or glass) within <see cref="MapConfig.RoofProbeCells"/> above its cell in a loaded chunk.
    /// </summary>
    public static bool IsRoofed(IMapCells cells, Vector3 eye)
    {
        var x = (int)MathF.Floor(eye.X);
        var z = (int)MathF.Floor(eye.Z);
        var eyeCell = (int)MathF.Floor(eye.Y);
        var checkedCy = int.MinValue;
        var loaded = false;
        for (var y = eyeCell + 1; y <= eyeCell + MapConfig.RoofProbeCells; y++)
        {
            var cy = y >> Chunk.SizeShift;
            if (cy != checkedCy)
            {
                checkedCy = cy;
                loaded = cells.StateOf(x >> Chunk.SizeShift, cy, z >> Chunk.SizeShift) == MapChunkState.Blocks;
            }
            if (loaded && BlockType.Occludes(cells.BlockAt(x, y, z))) return true;
        }
        return false;
    }

    private void QueueAround(int focusX, int focusZ, bool missingOnly)
    {
        const int r = MapConfig.ScanRadiusChunks;
        for (var dx = -r; dx <= r; dx++)
        for (var dz = -r; dz <= r; dz++)
        {
            if (dx * dx + dz * dz > r * r) continue;
            if (!missingOnly || !_tiles.ContainsKey((Layer, focusX + dx, focusZ + dz)))
                _dirty.Add((focusX + dx, focusZ + dz));
        }
    }

    // Rescans the nearest due columns; one beyond the scan radius is dropped (it comes back if the player returns)
    private void ScanDue(int focusX, int focusZ)
    {
        if (_dirty.Count == 0) return;
        const int radiusSq = MapConfig.ScanRadiusChunks * MapConfig.ScanRadiusChunks;
        _pick.Clear();
        foreach (var (x, z) in _dirty)
        {
            var distanceSq = (x - focusX) * (x - focusX) + (z - focusZ) * (z - focusZ);
            _pick.Add((x, z, distanceSq));
        }
        _pick.Sort((a, b) => a.DistanceSq.CompareTo(b.DistanceSq));

        var scanned = 0;
        foreach (var (x, z, distanceSq) in _pick)
        {
            if (distanceSq > radiusSq)
            {
                _dirty.Remove((x, z));
                continue;
            }
            if (scanned == MapConfig.TilesPerFrame) continue;
            _dirty.Remove((x, z));
            Scan(x, z);
            scanned++;
        }
    }

    // Scans into a spare tile and swaps it in only when it found something, so an explored tile whose chunks have
    // since unloaded is never blanked; the tile it replaces becomes the next spare
    private void Scan(int chunkX, int chunkZ)
    {
        var layer = Layer;
        _tiles.TryGetValue((layer, chunkX, chunkZ - 1), out var north);
        var fresh = _spare ?? new MapTile(chunkX, chunkZ);
        _spare = null;
        fresh.ChunkX = chunkX;
        fresh.ChunkZ = chunkZ;
        MapColumnScanner.Scan(cells, layer, fresh, north);
        if (!fresh.AnyKnown)
        {
            _spare = fresh;
            return;
        }

        var key = (layer, chunkX, chunkZ);
        _tiles.TryGetValue(key, out var replaced);
        _tiles[key] = fresh;
        _changed.Add(fresh);
        if (IsExplored(layer)) ExploredVersion++;
        if (replaced != null)
        {
            _spare = replaced;
            return;
        }
        // The tile to the south shades its top row against this one, which it found missing
        if (_tiles.ContainsKey((layer, chunkX, chunkZ + 1)))
            _dirty.Add((chunkX, chunkZ + 1));
    }

    private void DropLayer(MapLayer layer)
    {
        _pickKeys.Clear();
        foreach (var key in _tiles.Keys)
            if (key.Layer == layer) _pickKeys.Add(key);
        foreach (var key in _pickKeys)
            _tiles.Remove(key);
    }

    private void EvictFarthest(int focusX, int focusZ)
    {
        _pickKeys.Clear();
        _pickKeys.AddRange(_tiles.Keys);
        _pickKeys.Sort((a, b) => DistanceSq(b).CompareTo(DistanceSq(a)));
        var excess = _tiles.Count - MapConfig.MaxTiles * 9 / 10;
        for (var i = 0; i < excess; i++)
            _tiles.Remove(_pickKeys[i]);
        ExploredVersion++;
        return;

        int DistanceSq((MapLayer Layer, int X, int Z) key) =>
            (key.X - focusX) * (key.X - focusX) + (key.Z - focusZ) * (key.Z - focusZ);
    }
}
