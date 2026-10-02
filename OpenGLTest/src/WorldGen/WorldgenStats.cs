using System.Threading;

namespace silkgltriangle;

/// <summary>
/// Lock-free worldgen timing, fed from the chunk generation workers and the LOD builder thread
/// and drained once a second by <see cref="FrameProfiler"/> onto the console line (the
/// <c>wgen</c> token). Every quantity is kept twice: a window copy that
/// <see cref="TakeWindow"/> swaps back to zero, and a lifetime copy that only grows. Ticks are
/// <see cref="System.Diagnostics.Stopwatch"/> ticks. GL-free; any thread may add.
/// </summary>
public static class WorldgenStats
{
    // Window copies, zeroed by TakeWindow
    private static long _treeTicks;
    private static long _genTicks;
    private static long _genCount;
    private static long _growTicks;
    private static long _growCount;

    // Lifetime copies, never reset
    private static long _totalTreeTicks;
    private static long _totalGenTicks;
    private static long _totalGenCount;
    private static long _totalGrowTicks;
    private static long _totalGrowCount;

    /// <summary>One chunk's whole <see cref="Chunk.GenerateBlockData"/> call took <paramref name="ticks"/>.</summary>
    public static void AddChunk(long ticks)
    {
        Interlocked.Add(ref _genTicks, ticks);
        Interlocked.Increment(ref _genCount);
        Interlocked.Add(ref _totalGenTicks, ticks);
        Interlocked.Increment(ref _totalGenCount);
    }

    /// <summary>The tree/cactus stamp of one chunk took <paramref name="ticks"/>.</summary>
    public static void AddTreeTicks(long ticks)
    {
        Interlocked.Add(ref _treeTicks, ticks);
        Interlocked.Add(ref _totalTreeTicks, ticks);
    }

    /// <summary>One tree shape was built (a shape-cache miss) in <paramref name="ticks"/>.</summary>
    public static void AddGrow(long ticks)
    {
        Interlocked.Add(ref _growTicks, ticks);
        Interlocked.Increment(ref _growCount);
        Interlocked.Add(ref _totalGrowTicks, ticks);
        Interlocked.Increment(ref _totalGrowCount);
    }

    /// <summary>
    /// Returns everything added since the previous take and zeroes the window. Each field is
    /// swapped on its own, so an add racing the take lands in either this window or the next,
    /// never in neither.
    /// </summary>
    public static void TakeWindow(out long treeTicks, out long genTicks, out long chunks,
        out long growTicks, out long grows)
    {
        treeTicks = Interlocked.Exchange(ref _treeTicks, 0);
        genTicks = Interlocked.Exchange(ref _genTicks, 0);
        chunks = Interlocked.Exchange(ref _genCount, 0);
        growTicks = Interlocked.Exchange(ref _growTicks, 0);
        grows = Interlocked.Exchange(ref _growCount, 0);
    }

    public static long TotalTreeTicks => Volatile.Read(ref _totalTreeTicks);
    public static long TotalGenTicks => Volatile.Read(ref _totalGenTicks);
    public static long TotalChunks => Volatile.Read(ref _totalGenCount);
    public static long TotalGrowTicks => Volatile.Read(ref _totalGrowTicks);
    public static long TotalGrows => Volatile.Read(ref _totalGrowCount);
}
