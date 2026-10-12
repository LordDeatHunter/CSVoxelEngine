using System.Numerics;
using VoxelEngine.Data;

namespace VoxelEngine.Tests.Support;

/// <summary>
/// Builds the smallest world a chunk can be meshed in: the center chunk plus its six face
/// neighbors, all empty and marked ready. Shared by the meshing test classes.
/// </summary>
internal static class MeshTestWorld
{
    /// <summary>
    /// Chunk position far above the terrain, where the whole light volume around the center
    /// chunk reads sunlight 15. Tests that count merged quads sit here: light is part of the
    /// greedy key, and at the origin the sunlight follows the real terrain heights, so two
    /// adjacent cells may differ and stay separate quads for that reason alone.
    /// </summary>
    internal static readonly Vector3 HighAbove = new(0, 8, 0);

    internal static (ChunkManager Manager, Chunk Center) WalledArea() => WalledArea(Vector3.Zero);

    internal static (ChunkManager Manager, Chunk Center) WalledArea(Vector3 centerPosition)
    {
        var manager = new ChunkManager(null!);
        var center = new Chunk(null!, centerPosition, generateBlockData: false);
        center.IsBlockDataReady = true;
        manager.Chunks[centerPosition] = center;
        foreach (var offset in Vector3Directions.CardinalDirections)
        {
            var neighbor = new Chunk(null!, centerPosition + offset, generateBlockData: false);
            neighbor.IsBlockDataReady = true;
            manager.Chunks[centerPosition + offset] = neighbor;
        }
        return (manager, center);
    }

    internal static void FillAll(Chunk chunk, byte id)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(x, y, z, id, 0);
    }

    internal static void FillWalledArea(ChunkManager manager, byte id)
    {
        foreach (var chunk in manager.Chunks.Values) FillAll(chunk, id);
    }
}
