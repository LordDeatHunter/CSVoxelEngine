using System;

namespace VoxelEngine.Chunks;

/// <summary>
/// Where one world's raw cell writes are reported: a <see cref="ChunkManager"/> owns one and hands it to every chunk
/// it makes (<see cref="Chunk.WriteHook"/>), so a server observing its own world (<see cref="Written"/>) never hears
/// another world's writes, such as the client world of a player hosting from the game in the same process.
/// </summary>
public sealed class CellWriteHook
{
    /// <summary>Called with every raw cell write (world x, y, z, id, state); null when nobody observes. A server
    /// sets it to journal its writes for its clients.</summary>
    public Action<int, int, int, ushort, byte>? Written { get; set; }
}
