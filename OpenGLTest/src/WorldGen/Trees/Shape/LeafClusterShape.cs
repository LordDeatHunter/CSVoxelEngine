namespace VoxelEngine.WorldGen.Trees.Shape;

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

    /// <summary>
    /// A flat cap resting on the tip with its fronds drooping one level below (a palm): the pad's
    /// discs — <c>max(1, radius - 1)</c> at the tip's own level and <paramref name="radius"/> one
    /// above — plus the full disc of <paramref name="radius"/> one level below, so the crown has
    /// some depth. Nothing deeper, so the trunk under it stays bare.
    /// </summary>
    Fan = 3,
}
