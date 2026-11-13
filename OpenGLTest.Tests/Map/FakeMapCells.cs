using System.Collections.Generic;
using VoxelEngine.Effects.Climate;
using VoxelEngine.Map;

namespace VoxelEngine.Tests.Map;

/// <summary>A hand-built world for the map tests: cells set by hand, and only the chunks marked loaded readable.</summary>
internal sealed class FakeMapCells : IMapCells
{
    private readonly Dictionary<(int X, int Y, int Z), ushort> _cells = new();
    private readonly HashSet<(int X, int Y, int Z)> _loaded = [];
    private readonly Dictionary<(int X, int Y, int Z), int> _blocksPerChunk = new();

    public void Load(int chunkX, int chunkY, int chunkZ) => _loaded.Add((chunkX, chunkY, chunkZ));

    public void Unload(int chunkX, int chunkY, int chunkZ) => _loaded.Remove((chunkX, chunkY, chunkZ));

    public void Set(int x, int y, int z, ushort id)
    {
        var chunk = (x >> Chunk.SizeShift, y >> Chunk.SizeShift, z >> Chunk.SizeShift);
        var had = _cells.Remove((x, y, z));
        if (had) _blocksPerChunk[chunk]--;
        if (id == BlockType.Air) return;
        _cells[(x, y, z)] = id;
        _blocksPerChunk[chunk] = _blocksPerChunk.GetValueOrDefault(chunk) + 1;
    }

    /// <summary>Fills cells from <paramref name="bottomY"/> to <paramref name="topY"/> of one column.</summary>
    public void Column(int x, int z, int bottomY, int topY, ushort id)
    {
        for (var y = bottomY; y <= topY; y++) Set(x, y, z, id);
    }

    public MapChunkState StateOf(int chunkX, int chunkY, int chunkZ)
    {
        if (!_loaded.Contains((chunkX, chunkY, chunkZ))) return MapChunkState.Unloaded;
        return _blocksPerChunk.GetValueOrDefault((chunkX, chunkY, chunkZ)) > 0 ? MapChunkState.Blocks : MapChunkState.Empty;
    }

    public ushort BlockAt(int x, int y, int z) =>
        _loaded.Contains((x >> Chunk.SizeShift, y >> Chunk.SizeShift, z >> Chunk.SizeShift))
            ? _cells.GetValueOrDefault((x, y, z))
            : BlockType.Air;

    public bool TryTintFields(int chunkX, int chunkZ, out BiomeTintFields fields)
    {
        fields = default;
        return false;
    }
}
