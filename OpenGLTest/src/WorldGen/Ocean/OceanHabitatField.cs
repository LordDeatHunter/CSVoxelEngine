using VoxelEngine.Chunks;

namespace VoxelEngine.WorldGen.Ocean;

/// <summary>Generated ocean warmth at the four corners of one chunk column.</summary>
internal readonly record struct OceanHabitatField(float C00, float C10, float C01, float C11)
{
    /// <summary>Cheap own-column admission before four potentially cold generated cache reads.</summary>
    internal static bool HasRelevantOcean(ColumnData column)
    {
        for (var i = 0; i < column.Biomes.Length; i++)
            if (column.Biomes[i] == Biome.Ocean
                && column.WaterLevel[i] == WorldGenerationConfig.SeaLevel
                && WorldGenerationConfig.SeaLevel - column.Heights[i] >= 3)
                return true;
        return false;
    }

    /// <summary>Fill-owned generated corner reads; never used by pure classification or LOD.</summary>
    internal static OceanHabitatField ReadGeneratedCorners(int chunkX, int chunkZ) => new(
        ColumnCache.Get(chunkX, chunkZ).CornerWarmth,
        ColumnCache.Get(chunkX + 1, chunkZ).CornerWarmth,
        ColumnCache.Get(chunkX, chunkZ + 1).CornerWarmth,
        ColumnCache.Get(chunkX + 1, chunkZ + 1).CornerWarmth);

    /// <summary>Samples the shared corner lattice, including either far edge.</summary>
    internal float Sample(float localX, float localZ)
    {
        var fx = localX / Chunk.Size;
        var fz = localZ / Chunk.Size;
        return (1f - fx) * ((1f - fz) * C00 + fz * C01)
             + fx * ((1f - fz) * C10 + fz * C11);
    }

    /// <summary>A pure eligibility label; S6b supplies the reef and moss materials.</summary>
    internal OceanHabitat Classify(bool dynamic, bool ocean, int waterLevel, int floorY,
        float localX, float localZ)
    {
        var depth = WorldGenerationConfig.SeaLevel - floorY;
        if (!dynamic || !ocean || waterLevel != WorldGenerationConfig.SeaLevel || depth < 3)
            return OceanHabitat.None;

        var warmth = Sample(localX, localZ);
        if (depth >= 85) return OceanHabitat.Abyss;
        if (warmth <= 0.42f) return OceanHabitat.ColdShelf;
        if (warmth >= 0.58f && depth <= 24) return OceanHabitat.Reef;
        if (warmth < 0.58f && depth <= 40) return OceanHabitat.MeadowFloor;
        return OceanHabitat.DeepPlain;
    }
}

/// <summary>Pure generated-ocean habitat eligibility, without stored biome or block ids.</summary>
internal enum OceanHabitat : byte
{
    None,
    ColdShelf,
    Reef,
    MeadowFloor,
    DeepPlain,
    Abyss,
}
