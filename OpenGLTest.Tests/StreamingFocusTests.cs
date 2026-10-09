using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// <see cref="StreamingFocus"/> republishes its foci only when a focus changes chunk or the set
/// changes, which is what the chunk manager's reference-keyed unload gate and the workers' scan
/// frontiers both rely on, and its distance helpers measure against the nearest focus.
/// </summary>
public sealed class StreamingFocusTests
{
    private static Vector3 ChunkOf(Vector3 eye) => (eye / Chunk.Size).Floor();

    [Fact]
    public void AStillEyeRepublishesNothingAndAChunkCrossingPublishesOnce()
    {
        var focus = new StreamingFocus();
        Assert.Empty(focus.Chunks);

        // Away from the origin and on the negative side, so a truncation instead of a floor shows.
        var eye = new Vector3(-40.5f, 70.2f, 23.9f);
        focus.SetEye(eye);
        var published = focus.Chunks;
        Assert.Equal(new[] { ChunkOf(eye) }, published);

        // Moving inside the same chunk keeps the very same array.
        var sameChunk = eye + new Vector3(0.4f, -0.1f, 0.05f);
        Assert.Equal(ChunkOf(eye), ChunkOf(sameChunk));
        focus.SetEye(sameChunk);
        Assert.Same(published, focus.Chunks);

        // A chunk crossing publishes a new array once, and leaves the old snapshot untouched.
        var crossed = eye + new Vector3(Chunk.Size, 0f, 0f);
        focus.SetEye(crossed);
        var afterCrossing = focus.Chunks;
        Assert.NotSame(published, afterCrossing);
        Assert.Equal(new[] { ChunkOf(crossed) }, afterCrossing);
        Assert.Equal(new[] { ChunkOf(eye) }, published);
        focus.SetEye(crossed + new Vector3(0.1f, 0f, 0f));
        Assert.Same(afterCrossing, focus.Chunks);

        // The same holds for several eyes: the same set is the same array, a joiner is a new one,
        // and two eyes in one chunk are a single focus.
        var other = eye + new Vector3(0f, 0f, 20f * Chunk.Size);
        focus.SetEyes([crossed, other]);
        var two = focus.Chunks;
        Assert.Equal(new[] { ChunkOf(crossed), ChunkOf(other) }, two);
        focus.SetEyes([crossed, other + new Vector3(0.2f, 0f, 0f)]);
        Assert.Same(two, focus.Chunks);
        focus.SetEyes([crossed, crossed + new Vector3(0.1f, 0f, 0f), other]);
        Assert.Same(two, focus.Chunks);
        focus.SetEyes([crossed]);
        Assert.NotSame(two, focus.Chunks);
        Assert.Equal(new[] { ChunkOf(crossed) }, focus.Chunks);
    }

    [Fact]
    public void DistancesMeasureAgainstTheNearestFocus()
    {
        Vector3[] foci = [new(0f, 0f, 0f), new(30f, 0f, 0f)];
        var chunk = new Vector3(27f, 0f, 2f);
        var toFirst = Vector3.DistanceSquared(chunk, foci[0]);
        var toSecond = Vector3.DistanceSquared(chunk, foci[1]);
        Assert.True(toSecond < toFirst);

        Assert.Equal(toSecond, StreamingFocus.MinDistanceSquared(foci, chunk));
        Assert.Equal(toSecond, StreamingFocus.MinDistanceSquared([foci[1], foci[0]], chunk));
        Assert.Equal(float.PositiveInfinity, StreamingFocus.MinDistanceSquared([], chunk));

        // Inside the radius of the second focus only: within the set, outside the first alone.
        var radiusSq = (int)toSecond;
        Assert.True(toFirst > radiusSq);
        Assert.True(StreamingFocus.WithinRadius(foci, chunk, radiusSq));
        Assert.False(StreamingFocus.WithinRadius([foci[0]], chunk, radiusSq));
        Assert.False(StreamingFocus.WithinRadius([], chunk, radiusSq));
    }
}
