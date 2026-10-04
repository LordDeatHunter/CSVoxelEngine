namespace silkgltriangle;

/// <summary>
/// The 2D world generation inputs for one chunk column, every array indexed by
/// (x &lt;&lt; Chunk.SizeShift) | z. Computed once per column (see ColumnCache.Get) and shared
/// by the whole vertical chunk stack and the far LOD, so 2D noise is never re-evaluated per chunk.
/// </summary>
public sealed class ColumnData
{
    /// <summary>Terrain height: the top solid block before caves, with ravines already cut in.</summary>
    public readonly int[] Heights;

    /// <summary>
    /// Blocks of dirt between the grass and the stone. Normal columns are always at least 1;
    /// <c>0</c> means a bare stone column — no dirt <em>and</em> no grass, so the top block is
    /// stone too. Deep ravine walls use this so they read as exposed rock instead of terraces.
    /// </summary>
    public readonly byte[] DirtDepth;

    /// <summary>0..1; where it rises above 0 caves may reach the surface.</summary>
    public readonly float[] EntranceWeight;

    /// <summary>Shift applied to the cave thresholds so some areas are riddled and others nearly solid.</summary>
    public readonly float[] CaveIntensity;

    /// <summary>The biome of each column, same (x, z) indexing as <see cref="Heights"/>; decides
    /// which surface materials <see cref="Chunk.GenerateBlockData"/> lays down.</summary>
    public readonly Biome[] Biomes;

    /// <summary>
    /// The column's terrain height with ravines NOT cut in: the water body's own bed. It decides
    /// whether the column is wet (see <see cref="TerrainGenerator.IsWaterColumn"/>), so a ravine
    /// that merely deepens an otherwise dry column can never create water — only a bed that
    /// already sits below sea level can. <see cref="Heights"/> is this minus the ravine cut.
    /// </summary>
    public readonly int[] Bed;

    /// <summary>
    /// The y each column's water rises to (<see cref="TerrainGenerator.WaterLevelAt"/>), or
    /// <see cref="TerrainGenerator.NoWater"/> where the column holds none. A lake carries its own
    /// level here, which is what lets a pond sit in the ground it was dug into instead of on sea
    /// level. Stored rather than re-derived so the fill, the freezing pass and the far LOD read it
    /// without evaluating 2D noise again.
    /// </summary>
    public readonly int[] WaterLevel;

    /// <summary>
    /// The highest level in <see cref="WaterLevel"/>, or <see cref="TerrainGenerator.NoWater"/> when
    /// this chunk column holds no water at all. A chunk whose floor is above it has no water to
    /// fill, freeze or mesh and can skip the work.
    /// </summary>
    public readonly int MaxWaterLevel;

    public readonly int MinHeight;
    public readonly int MaxHeight;

    public ColumnData(int[] heights, byte[] dirtDepth, float[] entranceWeight, float[] caveIntensity,
        Biome[]? biomes = null, int[]? bed = null, int[]? waterLevel = null)
    {
        Heights = heights;
        DirtDepth = dirtDepth;
        EntranceWeight = entranceWeight;
        CaveIntensity = caveIntensity;
        Biomes = biomes ?? ForestBiomes(heights.Length);
        Bed = bed ?? heights;
        WaterLevel = waterLevel ?? NoWaterLevels(heights.Length);

        var maxWater = TerrainGenerator.NoWater;
        foreach (var level in WaterLevel)
            if (level > maxWater) maxWater = level;
        MaxWaterLevel = maxWater;

        var min = int.MaxValue;
        var max = int.MinValue;
        foreach (var h in heights)
        {
            if (h < min) min = h;
            if (h > max) max = h;
        }

        MinHeight = min;
        MaxHeight = max;
    }

    private static int[] NoWaterLevels(int length)
    {
        var levels = new int[length];
        for (var i = 0; i < length; i++)
            levels[i] = TerrainGenerator.NoWater;
        return levels;
    }

    private static Biome[] ForestBiomes(int length)
    {
        var biomes = new Biome[length];
        for (var i = 0; i < length; i++)
            biomes[i] = Biome.Forest;
        return biomes;
    }
}
