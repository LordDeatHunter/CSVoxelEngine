namespace silkgltriangle;

/// <summary>Cross-section of a species' wood: the pixel footprint a thickness is rasterised to.</summary>
public enum TrunkShape : byte
{
    /// <summary>A round cross-section (oak, spruce).</summary>
    Circle = 0,

    /// <summary>A square cross-section (cactus).</summary>
    Square = 1,
}
