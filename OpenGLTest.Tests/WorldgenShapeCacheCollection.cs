using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Serialises the tests that depend on <c>TreeGenerator.ShapeCache</c> instance identity or reset
/// it. <c>WorldgenTests.TrunkShapeIsCachedAcrossChunks</c> asserts that two adjacent
/// <c>ShapeOf</c> calls return the same instance, and <c>TreeWorldgenTests</c> calls
/// <c>TreeGenerator.ResetCaches</c> between two generation passes, which would falsify the
/// former if the two overlapped. xUnit runs test classes in parallel by default (there is no
/// <c>xunit.runner.json</c> here), so without this collection that interleaving is a rare flake.
/// Resetting is otherwise harmless to everyone else: every cached value is a pure function of
/// its key and is simply rebuilt.
/// </summary>
[CollectionDefinition("Worldgen shape cache")]
public sealed class WorldgenShapeCacheCollection;
