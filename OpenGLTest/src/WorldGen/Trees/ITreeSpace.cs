using VoxelEngine.WorldGen.Trees.Shape;

namespace VoxelEngine.WorldGen.Trees;

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

    /// <summary>
    /// The shell placement test of <see cref="TreeRasterizer"/>: whether a wide trunk's shell
    /// may stand in the cell. Defaults to <see cref="IsFree"/>; a live space may also accept the
    /// growing tree's own existing shells, so re-rasterising a grown tree keeps them.
    /// </summary>
    bool AcceptsShell(int x, int y, int z) => IsFree(x, y, z);
}
