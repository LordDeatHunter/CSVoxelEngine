namespace VoxelEngine.WorldGen.Structures;

/// <summary>Where <see cref="StructureLocator"/> found a structure and where a teleport to it lands.</summary>
/// <param name="X">The structure's center column on the X axis.</param>
/// <param name="Y">The level its ground floor stands at.</param>
/// <param name="Z">The structure's center column on the Z axis.</param>
/// <param name="LandX">The column a teleport lands on, on the X axis.</param>
/// <param name="LandFeetY">The y the feet stand at on the landing column.</param>
/// <param name="LandZ">The column a teleport lands on, on the Z axis.</param>
/// <param name="Distance">Horizontal blocks from the query column to (<paramref name="X"/>, <paramref name="Z"/>).</param>
public readonly record struct StructureLocation(int X, int Y, int Z, int LandX, int LandFeetY, int LandZ, double Distance);
