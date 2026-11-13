namespace VoxelEngine.Commands.Arguments;

/// <summary>A block or item an <see cref="ItemArgument"/> resolved: its id, and the name as it was typed (for the
/// answer).</summary>
public readonly record struct ContentItem(ushort Id, string Name);
