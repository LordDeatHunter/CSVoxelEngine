namespace silkgltriangle;

/// <summary>The volume a species' leaf cluster fills around a branch tip.</summary>
public enum LeafClusterShape : byte
{
    /// <summary>A rounded blob (broadleaf).</summary>
    Blob = 0,

    /// <summary>A downward-widening cone (conifer).</summary>
    Cone = 1,

    /// <summary>
    /// A flat cap resting on the tip (umbrella pads): a disc at the tip's own level and a slightly
    /// wider one just above it, nothing below, so the limb under the pad stays bare.
    /// </summary>
    Pad = 2,
}
