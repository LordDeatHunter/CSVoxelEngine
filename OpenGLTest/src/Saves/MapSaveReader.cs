using System.Collections.Generic;
using VoxelEngine.Map;

namespace VoxelEngine.Saves;

/// <summary>Reads the explored map and marks parser-rejected bytes for preservation on a later replacement.</summary>
internal static class MapSaveReader
{
    internal static IReadOnlyList<(MapLayerKind Kind, MapTile Tile)>? Read(ChunkSaveStore? store)
    {
        if (store == null) return null;
        var tiles = MapSaveCodec.TryParse(store.ReadFile(MapSaveCodec.FileName), out var rejected);
        if (rejected) store.MarkUnreadableFile(MapSaveCodec.FileName);
        return tiles;
    }
}
