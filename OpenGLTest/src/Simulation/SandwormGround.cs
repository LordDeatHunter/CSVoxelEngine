using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Simulation;

/// <summary>
/// What the sandworm reads of the ground (main thread; it reads the live chunk map): the surface height of a
/// column from the world source, whether the top cell is sand or sandstone (the only ground it will dash
/// across) and whether the column is desert.
/// </summary>
public static class SandwormGround
{
    private static byte _sandId;
    private static byte _sandstoneId;

    /// <summary>The y of the top face of the column's surface (the ground cell's y plus one) and whether that cell
    /// is dry sand or sandstone right now (a dug or built-over column reads as not sand).</summary>
    public static (float SurfaceY, bool OnSand) At(ChunkManager chunks, float x, float z)
    {
        var blockX = MathUtils.FloorToInt(x);
        var blockZ = MathUtils.FloorToInt(z);
        var column = chunks.WorldSource.GetColumn(VoxelCoords.WorldToChunk(blockX), VoxelCoords.WorldToChunk(blockZ));
        var i = VoxelCoords.ColumnIndex(VoxelCoords.WorldToLocal(blockX), VoxelCoords.WorldToLocal(blockZ));
        var height = column.Heights[i];
        var surface = height + 1f;
        if (height < column.WaterLevel[i]) return (surface, false);
        if (!chunks.TryGetCell(blockX, height, blockZ, out var id, out _)) return (surface, false);
        return (surface, IsSand(id));
    }

    /// <summary>Whether the column holding (<paramref name="x"/>, <paramref name="z"/>) is desert: the world
    /// source's own stored biome for a column it supplies, worldgen's undithered one otherwise.</summary>
    public static bool IsDesert(ChunkManager chunks, float x, float z)
    {
        var blockX = MathUtils.FloorToInt(x);
        var blockZ = MathUtils.FloorToInt(z);
        var chunkX = VoxelCoords.WorldToChunk(blockX);
        var chunkZ = VoxelCoords.WorldToChunk(blockZ);
        var source = chunks.WorldSource;
        if (!source.Supplies(chunkX, chunkZ)) return TerrainGenerator.BiomeAt(blockX, blockZ) == Biome.Desert;
        var i = VoxelCoords.ColumnIndex(VoxelCoords.WorldToLocal(blockX), VoxelCoords.WorldToLocal(blockZ));
        return source.GetColumn(chunkX, chunkZ).Biomes[i] == Biome.Desert;
    }

    /// <summary>Whether the block id is sand or sandstone.</summary>
    public static bool IsSand(byte id)
    {
        if (_sandId == 0)
        {
            _sandId = ContentRegistry.RequireBlock("sand").Id;
            _sandstoneId = ContentRegistry.RequireBlock("sandstone").Id;
        }
        return id == _sandId || id == _sandstoneId;
    }
}
