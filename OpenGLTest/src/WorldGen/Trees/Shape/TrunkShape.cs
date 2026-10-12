namespace VoxelEngine.WorldGen.Trees.Shape;

/// <summary>Cross-section of a species' wood: the pixel footprint a thickness is rasterized to.</summary>
public enum TrunkShape : byte
{
    /// <summary>A round cross-section (oak, spruce).</summary>
    Circle = 0,

    /// <summary>A square cross-section (cactus).</summary>
    Square = 1,
}
