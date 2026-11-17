namespace VoxelEngine.WorldGen;

/// <summary>
/// The terrain recipe selected for a saved world. Legacy is the generator shipped at the EA
/// master; Dynamic is the issue-64 recipe, whose visible terrain stages follow this foundation.
/// </summary>
public enum WorldGenProfile
{
    Legacy = 1,
    Dynamic = 2,
}
