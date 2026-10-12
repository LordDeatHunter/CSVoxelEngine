namespace VoxelEngine.WorldGen.Trees;

/// <summary>
/// A world cell read: the block id and state byte at (x, y, z), false when the cell's chunk is
/// not loaded. <c>ChunkManager.TryGetCell</c> matches it as a method group, which keeps the
/// tree engine free of <c>ChunkManager</c> (<see cref="TreeGraph.FromWorld"/> reads through it).
/// </summary>
public delegate bool TreeCellReader(int x, int y, int z, out byte id, out byte state);
