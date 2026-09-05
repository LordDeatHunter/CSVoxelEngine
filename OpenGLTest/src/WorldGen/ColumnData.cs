namespace silkgltriangle;

/// <summary>
/// The 2D world generation inputs for one chunk column, every array indexed by
/// (x &lt;&lt; Chunk.SizeShift) | z. Computed once per column (see Chunk.GetColumnData) and shared
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

    public readonly int MinHeight;
    public readonly int MaxHeight;

    public ColumnData(int[] heights, byte[] dirtDepth, float[] entranceWeight, float[] caveIntensity)
    {
        Heights = heights;
        DirtDepth = dirtDepth;
        EntranceWeight = entranceWeight;
        CaveIntensity = caveIntensity;

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
}
