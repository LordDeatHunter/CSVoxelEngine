using System;
using System.Threading;

namespace silkgltriangle;

/// <summary>
/// The world seed as runtime state. Every worldgen noise channel and hash derives from
/// <see cref="WorldGenerationConfig.Seed"/>, which reads <see cref="Value"/>; <see cref="Apply"/>
/// switches it between worlds and rebuilds everything that was derived from the old one.
/// </summary>
/// <remarks>
/// Worldgen statics are read unsynchronised by the chunk-generation workers, the LOD workers and
/// the mesh runners, so a swap is legal only while no <see cref="World"/> exists and all of those
/// threads have been stopped and joined. The writes are then published to the next world's
/// workers by <c>Thread.Start</c> (and were fenced from the old ones by <c>Thread.Join</c>), so no
/// field here needs to be volatile. Worldgen content (the block ids the generators resolve in
/// their static constructors) must already be loaded when <see cref="Apply"/> runs.
/// </remarks>
public static class WorldSeed
{
    /// <summary>The seed every run starts with, and the one tests and scripted runs rely on.</summary>
    public const int Default = 1337;

    private static int _value = Default;
    private static int _liveWorlds;

    /// <summary>The current world seed.</summary>
    public static int Value => _value;

    /// <summary>Registers a live world; <see cref="Apply"/> refuses to run until it ends.</summary>
    internal static void BeginWorld() => Interlocked.Increment(ref _liveWorlds);

    /// <summary>Ends a world registered by <see cref="BeginWorld"/>, after its workers are joined.</summary>
    internal static void EndWorld() => Interlocked.Decrement(ref _liveWorlds);

    /// <summary>Whether any world is registered, which makes <see cref="Apply"/> illegal.</summary>
    public static bool WorldIsLive => Volatile.Read(ref _liveWorlds) > 0;

    /// <summary>
    /// Sets the world seed, rebuilds every worldgen noise from it, clears the five worldgen caches
    /// and resets the process-global LOD edit state (column caps, felled-canopy markers, surface
    /// overrides). It always does the full reset, even when <paramref name="seed"/> equals the
    /// current seed, because a
    /// different world sharing the seed still needs the previous world's derived and edit state
    /// thrown away.
    /// </summary>
    /// <exception cref="InvalidOperationException">A world is live.</exception>
    public static void Apply(int seed)
    {
        if (WorldIsLive)
            throw new InvalidOperationException(
                "WorldSeed.Apply is legal only while no World exists: stop and join the chunk-generation " +
                "and LOD workers and drain the mesh runners first (worldgen statics are read " +
                "unsynchronised on those threads)");

        _value = seed;

        TerrainGenerator.Reseed();
        NoiseCaves.Reseed();
        CoalGenerator.Reseed();
        MalachiteGenerator.Reseed();
        LavaGenerator.Reseed();
        PebbleGenerator.Reseed();
        SnowGenerator.Reseed();
        FoliageGenerator.Reseed();
        GravelPocketGenerator.Reseed();
        UndergroundPoolGenerator.Reseed();

        // Every cached value is a function of (key, seed), so all of them are wrong now.
        ColumnCache.Reset();
        WormCarver.ResetCellCache();
        TreeGenerator.ResetCaches();

        // The previous world's edit state is process-global and must not leak into the next one.
        LodRegion.ResetColumnCaps();
        TreeGenerator.ResetCanopyDisturbed();
        LodSurface.Reset();
    }
}
