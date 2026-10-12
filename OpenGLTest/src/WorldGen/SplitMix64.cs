namespace VoxelEngine.WorldGen;

/// <summary>
/// Small deterministic PRNG (SplitMix64) for world generation. Unlike System.Random its
/// sequence is fixed by this code alone, so features seeded from world coordinates come out
/// identical no matter which thread or chunk asks for them.
/// </summary>
public sealed class SplitMix64(ulong seed)
{
    private ulong _state = seed;

    /// <summary>One round of the SplitMix64 finalizer; also a good integer hash on its own.</summary>
    public static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Hashes a 2D cell coordinate and the seed into a stream seed.</summary>
    public static ulong Hash(int x, int z, int seed) =>
        Mix(((ulong)(uint)x << 32) | (uint)z) ^ Mix((ulong)(uint)seed * 0x9E3779B97F4A7C15UL);

    public ulong NextULong()
    {
        _state += 0x9E3779B97F4A7C15UL;
        return Mix(_state);
    }

    /// <summary>Uniform in [0, 1).</summary>
    public float NextFloat() => (NextULong() >> 40) * (1f / (1 << 24));

    /// <summary>Uniform in [min, max).</summary>
    public float NextFloat(float min, float max) => min + (max - min) * NextFloat();

    /// <summary>Uniform integer in [0, maxExclusive).</summary>
    public int NextInt(int maxExclusive) => (int)(NextULong() % (ulong)maxExclusive);

    /// <summary>Uniform integer in [min, maxExclusive).</summary>
    public int NextInt(int min, int maxExclusive) => min + NextInt(maxExclusive - min);
}
