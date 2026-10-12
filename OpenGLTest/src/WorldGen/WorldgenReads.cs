using System.Threading;
using VoxelEngine.Chunks;

namespace VoxelEngine.WorldGen;

/// <summary>
/// A test hook counting the engine's entries into worldgen, so a test can prove a path (a
/// multiplayer client's mesh build) made none. Noted at the two choke points: a
/// <see cref="ColumnCache"/> miss (once per column, never per cache hit) and
/// <see cref="TerrainGenerator.Climate"/>. Off by default, so production pays one volatile bool
/// read there; only <c>ClientWorldgenIsolationTests</c> sets <see cref="Enabled"/>, which runs
/// serialized in the "Worldgen seed" test collection and clears it again in a finally.
/// </summary>
internal static class WorldgenReads
{
    /// <summary>Whether <see cref="Note"/> counts. Test-only.</summary>
    internal static volatile bool Enabled;

    private static long _count;

    /// <summary>Worldgen entries noted while <see cref="Enabled"/> was set.</summary>
    internal static long Count => Volatile.Read(ref _count);

    /// <summary>Records one worldgen entry when counting is enabled.</summary>
    internal static void Note()
    {
        if (Enabled) Interlocked.Increment(ref _count);
    }
}
