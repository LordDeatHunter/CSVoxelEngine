namespace silkgltriangle;

/// <summary>The silhouette the far LOD draws a species' crown as.</summary>
public enum LodCrownProfile : byte
{
    /// <summary>A rounded dome (broadleaf).</summary>
    Dome = 0,

    /// <summary>A pointed cone (conifer).</summary>
    Cone = 1,

    /// <summary>A straight column (cactus).</summary>
    Column = 2,
}
