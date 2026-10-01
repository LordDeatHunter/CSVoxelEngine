namespace silkgltriangle;

/// <summary>The volume a species' leaf cluster fills around a branch tip.</summary>
public enum LeafClusterShape : byte
{
    /// <summary>A rounded blob (broadleaf).</summary>
    Blob = 0,

    /// <summary>A downward-widening cone (conifer).</summary>
    Cone = 1,
}
