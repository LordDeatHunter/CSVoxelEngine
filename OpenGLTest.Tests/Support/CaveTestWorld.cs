using System;
using System.Numerics;
using VoxelEngine.Player;
using Xunit;

namespace VoxelEngine.Tests.Support;

/// <summary>
/// A hand-built cave for the cave spawn searches: solid stone chunks over x and z -48..47 and the chunk layer
/// holding the floor, with a cave three cells tall (y = floor + 1 .. floor + 3) carved across all of it, far
/// under the generated terrain, its floor layer made of one block. Every chunk publishes one sun light (0..15) and
/// no block light for every cell.
/// </summary>
internal static class CaveTestWorld
{
    /// <summary>The default floor: well under the generated terrain at the origin, far above hell.</summary>
    public const int Floor = -25;

    /// <summary>The cave over <paramref name="floor"/> (a floor layer of <paramref name="floorId"/>, stone when 0),
    /// every cell lit with <paramref name="sun"/> light.</summary>
    public static ChunkManager Cave(int sun, int floor = Floor, ushort floorId = 0)
    {
        var cy = floor >> Chunk.SizeShift;
        Assert.Equal(cy, (floor + 3) >> Chunk.SizeShift);
        var chunks = new ChunkManager(null!);
        var stone = ContentRegistry.RequireBlock("stone").Id;
        if (floorId == 0) floorId = stone;
        var light = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
        Array.Fill(light, (byte)(sun << 4));
        for (var cx = -3; cx <= 2; cx++)
        for (var cz = -3; cz <= 2; cz++)
        {
            var pos = new Vector3(cx, cy, cz);
            var chunk = new Chunk(null!, pos, generateBlockData: false) { IsBlockDataReady = true };
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var worldY = cy * Chunk.Size + y;
                var open = worldY > floor && worldY <= floor + 3;
                chunk.SetBlockRaw(x, y, z, open ? BlockType.Air : worldY == floor ? floorId : stone, 0);
            }
            chunk.PublishLight(light);
            chunks.Chunks[pos] = chunk;
        }
        return chunks;
    }

    /// <summary>The eye of a body standing on the cave floor <paramref name="floor"/> at the origin.</summary>
    public static Vector3 EyeOnFloor(int floor = Floor) => new(0.5f, floor + 1 + Camera.EyeHeight, 0.5f);
}
