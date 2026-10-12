using System.Numerics;
using silkgltriangle.Content;

namespace silkgltriangle.Tests;

/// <summary>Small headless worlds the mob tests (slimes, goblins) stand their bodies in.</summary>
internal static class MobTestWorld
{
    /// <summary>One loaded chunk at the origin whose bottom layer (y = 0) is <paramref name="topId"/>,
    /// so a body stands with its feet at y = 1.</summary>
    public static ChunkManager Floor(byte topId)
    {
        var chunks = new ChunkManager(null!);
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        chunks.Chunks[Vector3.Zero] = chunk;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(x, 0, z, topId, 0);
        return chunks;
    }

    /// <summary>A stone floor with source water stacked on top from y = 1 to
    /// <paramref name="waterCells"/>; a body spawned at the floor is submerged.</summary>
    public static ChunkManager Pool(int waterCells)
    {
        var chunks = Floor(ContentRegistry.RequireBlock("stone").Id);
        var chunk = chunks.Chunks[Vector3.Zero];
        var water = ContentRegistry.RequireBlock("water").Id;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        for (var y = 1; y <= waterCells; y++)
            chunk.SetBlockRaw(x, y, z, water, 0);
        return chunks;
    }

    /// <summary>
    /// The surface layer of a 7x7 chunk area at each column's real worldgen height, so a spawn
    /// ring finds solid loaded ground and two clear cells above every dry column.
    /// </summary>
    public static ChunkManager Surface()
    {
        var chunks = new ChunkManager(null!);
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var cx = -3; cx <= 3; cx++)
        for (var cz = -3; cz <= 3; cz++)
        {
            var heights = ColumnCache.GetHeights(cx, cz);
            for (var lx = 0; lx < Chunk.Size; lx++)
            for (var lz = 0; lz < Chunk.Size; lz++)
            {
                var height = heights[(lx << Chunk.SizeShift) | lz];
                var cy = height >> Chunk.SizeShift;
                var pos = new Vector3(cx, cy, cz);
                if (!chunks.Chunks.TryGetValue(pos, out var chunk))
                {
                    chunk = new Chunk(null!, pos, generateBlockData: false);
                    chunk.IsBlockDataReady = true;
                    chunks.Chunks[pos] = chunk;
                }
                chunk.SetBlockRaw(lx, height & Chunk.SizeMask, lz, stone, 0);
            }
        }
        return chunks;
    }
}
