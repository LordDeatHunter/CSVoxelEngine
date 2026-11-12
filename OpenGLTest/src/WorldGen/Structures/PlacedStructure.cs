using VoxelEngine.Content.Structures;

namespace VoxelEngine.WorldGen.Structures;

/// <summary>
/// A template placed in the world: centered on column (<see cref="X"/>, <see cref="Z"/>), its bottom layer at world y
/// <see cref="OriginY"/>, turned <see cref="Turns"/> quarter turns clockwise. The footprint spans
/// <see cref="MinX"/>..<see cref="MaxX"/> and <see cref="MinZ"/>..<see cref="MaxZ"/>, inclusive.
/// </summary>
public readonly record struct PlacedStructure(StructureTemplate Template, int Turns, int X, int OriginY, int Z)
{
    /// <summary>The footprint's west column.</summary>
    public int MinX => StructureStamper.OriginOf(X, Template);

    /// <summary>The footprint's east column.</summary>
    public int MaxX => MinX + Template.Size - 1;

    /// <summary>The footprint's north row.</summary>
    public int MinZ => StructureStamper.OriginOf(Z, Template);

    /// <summary>The footprint's south row.</summary>
    public int MaxZ => MinZ + Template.Size - 1;

    /// <summary>The world y of the layer standing on the anchor's floor.</summary>
    public int GroundY => OriginY + Template.GroundLayer;
}
