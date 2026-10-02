namespace silkgltriangle;

/// <summary>
/// The obstacle view a tree grows against: whether a tree may write wood or leaves into a cell.
/// Implementations are read by one grower at a time and need not be thread-safe.
/// <see cref="VirtualTreeSpace"/> is the worldgen view (terrain heightmap plus the cells other
/// trees already took); <see cref="WorldTreeSpace"/> is the live-world view reactive growth reads
/// the loaded chunks through.
/// </summary>
public interface ITreeSpace
{
    /// <summary>Whether a tree may write wood or leaves into the cell.</summary>
    bool IsFree(int x, int y, int z);
}
