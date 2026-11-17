using System.Collections.Concurrent;
using System.Threading;

namespace VoxelEngine.Chunks;

/// <summary>
/// The cells of the doors that are mid-swing right now. Such a cell is drawn and collided by its moving panel
/// alone, so the mesher leaves its model out of the chunk mesh and the body sweeps skip its boxes until the swing
/// ends (<c>DoorSwings</c> owns the entries). A bellows that is pressed down is in here too (<c>BellowsSquash</c>):
/// the mesher leaves it to the smithing pass, which draws it squashed, while its collision stays its block's own.
/// Written on the main thread and read by the mesh workers, so it is
/// thread-safe, and a world with no door swinging answers from one counter.
/// </summary>
public sealed class SwingingCells
{
    private readonly ConcurrentDictionary<(int X, int Y, int Z), byte> _cells = new();
    private int _count;

    /// <summary>Whether the cell belongs to a door that is mid-swing.</summary>
    public bool Contains(int x, int y, int z) => Volatile.Read(ref _count) != 0 && _cells.ContainsKey((x, y, z));

    /// <summary>Marks the cell as mid-swing.</summary>
    public void Add(int x, int y, int z)
    {
        if (_cells.TryAdd((x, y, z), 0))
            Interlocked.Increment(ref _count);
    }

    /// <summary>Hands the cell back to the chunk mesh and the block collision.</summary>
    public void Remove(int x, int y, int z)
    {
        if (_cells.TryRemove((x, y, z), out _))
            Interlocked.Decrement(ref _count);
    }
}
