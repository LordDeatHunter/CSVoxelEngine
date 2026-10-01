namespace silkgltriangle;

/// <summary>How a trunk wider than one block meets the cell grid at its rim.</summary>
public enum TrunkRim : byte
{
    /// <summary>The rim snaps to whole blocks (shell cells are full cubes).</summary>
    Block = 0,

    /// <summary>The rim follows the pixel outline (shell cells carry partial thickness).</summary>
    Pixel = 1,
}
