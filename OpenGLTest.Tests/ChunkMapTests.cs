using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Pins <see cref="ChunkMap"/>'s contract - the wrapper that keeps <c>Vector3</c> as the outward
/// chunk key while storing entries under a packed <c>long</c>. These facts deliberately assert
/// the OUTWARD behavior only; the packing itself is pinned by <c>ChunkKeyTests</c>.
/// <para>
/// Negative controls. Each fact below fails against a specific way the wrapper can be written
/// wrongly: an overload forwarding to the wrong dictionary operation (set-then-get), the
/// <c>int</c> overloads packing their arguments in a different order or with a different sign
/// convention from the <c>Vector3</c> ones (the agreement facts), a "set" implemented as an add
/// that leaves the old entry behind (the overwrite fact), <c>TryRemove</c> reporting success on
/// an absent key or failing to drop the entry (the removal fact), <c>Values</c> handing back the
/// keys or a stale snapshot, and a packer whose sign handling collapses <c>+n</c> and <c>-n</c>
/// onto one key (the sign fact).
/// </para>
/// </summary>
public sealed class ChunkMapTests
{
    public ChunkMapTests() => TestContent.EnsureLoaded();

    private static Chunk At(Vector3 pos) => new(null!, pos, generateBlockData: false);

    [Fact]
    public void ChunksRoundTripThroughTheIndexerIncludingNegativePositions()
    {
        var map = new ChunkMap();
        var positions = new[]
        {
            Vector3.Zero,
            new Vector3(1, 2, 3),
            new Vector3(-1, -2, -3),
            new Vector3(-17, 0, 24),
            new Vector3(24, -1, -17)
        };

        var chunks = positions.Select(At).ToArray();
        for (var i = 0; i < positions.Length; i++) map[positions[i]] = chunks[i];

        Assert.Equal(positions.Length, map.Count);
        for (var i = 0; i < positions.Length; i++)
            Assert.Same(chunks[i], map[positions[i]]);
    }

    [Fact]
    public void TheVectorAndIntegerContainsKeyOverloadsAgree()
    {
        var map = new ChunkMap();
        map[new Vector3(-5, 3, -9)] = At(new Vector3(-5, 3, -9));

        Assert.True(map.ContainsKey(new Vector3(-5, 3, -9)));
        Assert.True(map.ContainsKey(-5, 3, -9));

        // A position that was never written answers false through both spellings
        Assert.False(map.ContainsKey(new Vector3(-5, 3, 9)));
        Assert.False(map.ContainsKey(-5, 3, 9));
    }

    [Fact]
    public void TheVectorAndIntegerTryGetValueOverloadsReturnTheSameChunk()
    {
        var map = new ChunkMap();
        var chunk = At(new Vector3(-8, -1, 17));
        map[new Vector3(-8, -1, 17)] = chunk;

        Assert.True(map.TryGetValue(new Vector3(-8, -1, 17), out var byVector));
        Assert.True(map.TryGetValue(-8, -1, 17, out var byInts));
        Assert.Same(chunk, byVector);
        Assert.Same(chunk, byInts);

        Assert.False(map.TryGetValue(new Vector3(-8, -1, 18), out var missingByVector));
        Assert.False(map.TryGetValue(-8, -1, 18, out var missingByInts));
        Assert.Null(missingByVector);
        Assert.Null(missingByInts);
    }

    [Fact]
    public void WritingTwiceToOnePositionReplacesRatherThanDuplicates()
    {
        var map = new ChunkMap();
        var first = At(Vector3.Zero);
        var second = At(Vector3.Zero);

        map[Vector3.Zero] = first;
        map[Vector3.Zero] = second;

        Assert.Equal(1, map.Count);
        Assert.Same(second, map[Vector3.Zero]);
        Assert.Single(map.Values);
    }

    [Fact]
    public void TryRemoveHandsBackTheChunkAndThenReportsItGone()
    {
        var map = new ChunkMap();
        var chunk = At(new Vector3(2, -2, 2));
        map[new Vector3(2, -2, 2)] = chunk;

        Assert.True(map.TryRemove(new Vector3(2, -2, 2), out var removed));
        Assert.Same(chunk, removed);
        Assert.Equal(0, map.Count);
        Assert.False(map.ContainsKey(new Vector3(2, -2, 2)));

        Assert.False(map.TryRemove(new Vector3(2, -2, 2), out var again));
        Assert.Null(again);
    }

    [Fact]
    public void ValuesHoldsExactlyTheChunksThatWereWritten()
    {
        var map = new ChunkMap();
        var a = At(new Vector3(0, 0, 0));
        var b = At(new Vector3(1, 0, 0));
        var c = At(new Vector3(0, 0, -1));
        map[new Vector3(0, 0, 0)] = a;
        map[new Vector3(1, 0, 0)] = b;
        map[new Vector3(0, 0, -1)] = c;

        var values = map.Values.ToList();
        Assert.Equal(3, values.Count);
        Assert.Contains(a, values);
        Assert.Contains(b, values);
        Assert.Contains(c, values);

        map.TryRemove(new Vector3(1, 0, 0), out _);
        var afterRemoval = map.Values.ToList();
        Assert.Equal(2, afterRemoval.Count);
        Assert.DoesNotContain(b, afterRemoval);
    }

    [Fact]
    public void PositionsDifferingOnlyInSignOnOneAxisAreDistinctEntries()
    {
        var map = new ChunkMap();
        var positive = At(new Vector3(0, 0, 7));
        var negative = At(new Vector3(0, 0, -7));

        map[new Vector3(0, 0, 7)] = positive;
        map[new Vector3(0, 0, -7)] = negative;

        Assert.Equal(2, map.Count);
        Assert.Same(positive, map[new Vector3(0, 0, 7)]);
        Assert.Same(negative, map[new Vector3(0, 0, -7)]);

        // And the same on the other two axes, since each is masked separately
        map[new Vector3(7, 0, 0)] = At(new Vector3(7, 0, 0));
        map[new Vector3(-7, 0, 0)] = At(new Vector3(-7, 0, 0));
        map[new Vector3(0, 7, 0)] = At(new Vector3(0, 7, 0));
        map[new Vector3(0, -7, 0)] = At(new Vector3(0, -7, 0));
        Assert.Equal(6, map.Count);
    }
}
