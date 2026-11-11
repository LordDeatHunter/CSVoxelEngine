using Xunit;

namespace VoxelEngine.Tests.Support;

/// <summary>
/// Tests that load a different content set into the process-global registry
/// (<see cref="TestContent.SwapIn"/>): the ids, the per-id <see cref="BlockType"/> tables, the item
/// registry and the baked models are shared by the whole test process, so a swap while another
/// collection reads them would corrupt that collection's results. xUnit runs a collection that
/// disables parallelization alone, after every parallel collection has finished, which is the only
/// thing that makes the swap safe. Every test in it calls <see cref="TestContent.RestoreReal"/> in a
/// <c>finally</c>, so whatever runs next sees the real content again.
/// </summary>
[CollectionDefinition("Content registry swap", DisableParallelization = true)]
public sealed class ContentRegistrySwapCollection;
