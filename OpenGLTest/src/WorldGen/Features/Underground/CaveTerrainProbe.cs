using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.WorldGen.Features.Underground;

/// <summary>Pure completed terrain for bounded lake planning, including sea, geodes and
/// structures, without cave life that a lake displaces. Each query owns scratch and generates
/// without lakes, so it cannot
/// recurse, overwrite a worker's active scratch or depend on loaded chunks/player edits.</summary>
internal sealed class CaveTerrainProbe
{
    private readonly Dictionary<(int X, int Y, int Z), ushort[]> _chunks = new();

    internal ushort At(int x, int y, int z)
    {
        var key = (x >> Chunk.SizeShift, y >> Chunk.SizeShift, z >> Chunk.SizeShift);
        if (!_chunks.TryGetValue(key, out var blocks))
        {
            blocks = Generate(key.Item1, key.Item2, key.Item3);
            _chunks.Add(key, blocks);
        }
        return blocks[Chunk.Idx(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask)];
    }

    internal static ushort[] Generate(int cx, int cy, int cz)
    {
        var chunk = new Chunk(null!, new Vector3(cx, cy, cz), false);
        ChunkTerrainFill.GenerateBlockData(chunk, generateLakes: false);
        return chunk.Storage.ToDense();
    }
}
