namespace VoxelEngine.WorldGen.Trees.Shape;

/// <summary>
/// One shell cell of a wide trunk, relative to its core (same Y), plus its parent direction in
/// <see cref="TreeDirections"/> order: one step toward the core, so a diagonal shell chains
/// through an edge shell. Built once into <see cref="TreeRasterizer.ShellOffsets"/>.
/// </summary>
public readonly record struct ShellOffset(int Dx, int Dz, int ParentDir);
