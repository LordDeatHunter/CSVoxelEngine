namespace VoxelEngine.WorldGen.Trees.Shape;

/// <summary>
/// One cell a rasterised tree writes: the world cell, the block id and the per-block state byte
/// (<see cref="WoodState"/> for wood, <see cref="LeafState"/> for leaves).
/// </summary>
public readonly record struct TreeCell(int X, int Y, int Z, byte Block, byte State);
