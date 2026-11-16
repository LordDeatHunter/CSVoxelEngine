using System.Numerics;
using VoxelEngine.Data;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Tests.Support;

/// <summary>
/// Builds the smallest world a chunk can be meshed in: the center chunk plus its six face
/// neighbors, all empty and marked ready. Shared by the meshing test classes.
/// </summary>
internal static class MeshTestWorld
{
    /// <summary>
    /// Chunk position far above the generated terrain. This alone does not make light uniform:
    /// opaque test geometry casts real shadows below itself.
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

    /// <summary>
    /// A dark room for geometry tests whose expected greedy count assumes equal light keys.
    /// The stone roof spans the whole light volume, and the matching column source places
    /// its terrain height at the roof. Other mesh tests keep their normal sky and roof inputs.
    /// </summary>
    internal static (ChunkManager Manager, Chunk Center) DarkRoofArea(Vector3 centerPosition)
    {
        var (manager, center) = WalledArea(centerPosition);
        var roofY = ((int)centerPosition.Y + 1) * Chunk.Size;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        manager.ColumnSource = new FlatWorldSource((int)centerPosition.X - 1, (int)centerPosition.X + 1,
            (int)centerPosition.Z - 1, (int)centerPosition.Z + 1, roofY, stone,
            Biome.Forest, warmth: 0.5f, wetness: 0.5f);

        for (var dx = -1; dx <= 1; dx++)
        for (var dz = -1; dz <= 1; dz++)
        {
            var position = centerPosition + new Vector3(dx, 1, dz);
            if (!manager.Chunks.TryGetValue(position, out var roof))
            {
                roof = new Chunk(null!, position, generateBlockData: false);
                roof.IsBlockDataReady = true;
                manager.Chunks[position] = roof;
            }
            for (var x = 0; x < Chunk.Size; x++)
            for (var z = 0; z < Chunk.Size; z++)
                roof.SetBlockRaw(x, 0, z, stone, 0);
        }
        return (manager, center);
    }

    internal static void FillAll(Chunk chunk, ushort id)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(x, y, z, id, 0);
    }

    internal static void FillWalledArea(ChunkManager manager, ushort id)
    {
        foreach (var chunk in manager.Chunks.Values) FillAll(chunk, id);
    }
}
