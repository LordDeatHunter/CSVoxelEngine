using System;
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
    private static ushort _sandId;
    private static ushort _sandstoneId;

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
        // The worm burrows through any ground; only open water stops it
        return (surface, height >= column.WaterLevel[i]);
    }

    /// <summary>Whether (<paramref name="x"/>, <paramref name="z"/>) is desert or within
    /// <paramref name="reach"/> blocks of it (sampled on a ring of eight points).</summary>
    public static bool IsNearDesert(ChunkManager chunks, float x, float z, float reach)
    {
        if (IsDesert(chunks, x, z)) return true;
        for (var k = 0; k < 8; k++)
        {
            var angle = k * MathF.PI / 4f;
            if (IsDesert(chunks, x + MathF.Cos(angle) * reach, z + MathF.Sin(angle) * reach)) return true;
        }

        return false;
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

    /// <summary>The sand block id, for the chips a surfacing worm throws.</summary>
    public static ushort SandId
    {
        get
        {
            IsSand(0);
            return _sandId;
        }
    }

    /// <summary>Whether the block id is sand or sandstone.</summary>
    public static bool IsSand(ushort id)
    {
        if (_sandId == 0)
        {
            _sandId = ContentRegistry.RequireBlock("sand").Id;
            _sandstoneId = ContentRegistry.RequireBlock("sandstone").Id;
        }
        return id == _sandId || id == _sandstoneId;
    }
}
