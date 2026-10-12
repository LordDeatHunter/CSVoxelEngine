using Xunit;

namespace VoxelEngine.Tests.Support;

/// <summary>
/// Tests that swap the process-global world seed (<see cref="WorldSeed.Apply"/>). The seed and
/// every noise and cache derived from it are shared by the whole test process, so a swap while
/// another collection generates terrain would corrupt that collection's results. xUnit runs a
/// collection that disables parallelisation alone, after every parallel collection has finished,
/// which is the only thing that makes the swap safe. Every test in it restores
/// <see cref="WorldSeed.Default"/> in a <c>finally</c>, so whatever runs next sees the default world.
/// </summary>
[CollectionDefinition("Worldgen seed", DisableParallelization = true)]
public sealed class WorldgenSeedCollection;
