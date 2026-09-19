using System.Collections.Generic;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// <c>ChunkKey</c> is the pure, GL-free arithmetic the chunk dictionaries are rekeyed on, so
/// every way it can go silently wrong is pinned here against exact hand-checkable numbers
/// rather than left to a rendering difference to reveal. Each fact is a negative control for
/// one specific source mutation, named in its own comment:
/// <list type="bullet">
/// <item>dropping <c>SignExtend</c> from <c>Unpack</c> (negative coordinates come back huge);</item>
/// <item>spelling <c>SignExtend</c> with a logical shift instead of an arithmetic one (the
/// -1/0/+1 boundary breaks first);</item>
/// <item>shifting by the wrong multiple of <c>PackBits</c> (the axes bleed into each other);</item>
/// <item>widening or narrowing <c>PackMask</c> (the true edges and the collision sweep move);</item>
/// <item>truncating instead of flooring in <c>PackBlock</c> — the floor-versus-truncate trap,
/// which merges chunk -0 into chunk 0 and which the replay probe already proved is otherwise
/// invisible to every structural assertion;</item>
/// <item>an axis swap or a wrong per-component conversion in the <c>Vector3</c> overload.</item>
/// </list>
/// The range is deliberately asymmetric: 21 bits signed is -2^20..2^20-1, so there is a fact
/// asserting that +2^20 does NOT round-trip. That is the packer's contract, not a defect.
/// </summary>
public sealed class ChunkKeyTests
{
    private static (int X, int Y, int Z) RoundTrip(int cx, int cy, int cz)
    {
        ChunkKey.Unpack(ChunkKey.Pack(cx, cy, cz), out var ox, out var oy, out var oz);
        return (ox, oy, oz);
    }

    [Fact]
    public void PositiveChunkCoordinatesRoundTripExactly()
    {
        Assert.Equal((0, 0, 0), RoundTrip(0, 0, 0));
        Assert.Equal((1, 2, 3), RoundTrip(1, 2, 3));
        Assert.Equal((17, 5, 24), RoundTrip(17, 5, 24));
        Assert.Equal((1000, 999, 998), RoundTrip(1000, 999, 998));
    }

    // Negative control for a missing SignExtend: without it these come back as 2^21 - n.
    [Fact]
    public void NegativeChunkCoordinatesRoundTripExactlyOnEveryAxis()
    {
        Assert.Equal((-1, -2, -3), RoundTrip(-1, -2, -3));
        Assert.Equal((-17, 5, 24), RoundTrip(-17, 5, 24));
        Assert.Equal((17, -5, 24), RoundTrip(17, -5, 24));
        Assert.Equal((17, 5, -24), RoundTrip(17, 5, -24));
        Assert.Equal((-1000, -999, -998), RoundTrip(-1000, -999, -998));
    }

    // Negative control for a logical rather than arithmetic shift in SignExtend: -1 is the
    // all-ones case and is the first thing an unsigned shift gets wrong.
    [Fact]
    public void TheSignBoundaryRoundTripsOnEveryAxis()
    {
        foreach (var v in new[] { -1, 0, 1 })
        {
            Assert.Equal((v, 0, 0), RoundTrip(v, 0, 0));
            Assert.Equal((0, v, 0), RoundTrip(0, v, 0));
            Assert.Equal((0, 0, v), RoundTrip(0, 0, v));
        }
    }

    // The TRUE edges of a signed 21-bit field: -2^20 and 2^20 - 1, not +/-2^20.
    [Fact]
    public void TheRepresentableEdgesRoundTripOnEveryAxis()
    {
        const int min = -(1 << 20); // -1,048,576
        const int max = (1 << 20) - 1; // +1,048,575

        Assert.Equal((min, 0, 0), RoundTrip(min, 0, 0));
        Assert.Equal((0, min, 0), RoundTrip(0, min, 0));
        Assert.Equal((0, 0, min), RoundTrip(0, 0, min));

        Assert.Equal((max, 0, 0), RoundTrip(max, 0, 0));
        Assert.Equal((0, max, 0), RoundTrip(0, max, 0));
        Assert.Equal((0, 0, max), RoundTrip(0, 0, max));

        Assert.Equal((min, max, min), RoundTrip(min, max, min));
        Assert.Equal((max, min, max), RoundTrip(max, min, max));
    }

    // The contract, stated as a fact so nobody writes the +2^20 round trip that would fail:
    // one past the positive edge wraps to the negative edge. The edge is 16,777,215 blocks out,
    // far beyond anything the streamer loads, so this is documented rather than defended against.
    [Fact]
    public void OnePastThePositiveEdgeDeliberatelyWrapsToTheNegativeEdge()
    {
        const int overflow = 1 << 20; // +1,048,576, one past the representable maximum
        const int wrapped = -(1 << 20);

        Assert.Equal((wrapped, 0, 0), RoundTrip(overflow, 0, 0));
        Assert.Equal((0, wrapped, 0), RoundTrip(0, overflow, 0));
        Assert.Equal((0, 0, wrapped), RoundTrip(0, 0, overflow));

        // ...and it is the same key as the negative edge, which is exactly why the range stops here.
        Assert.Equal(ChunkKey.Pack(wrapped, 0, 0), ChunkKey.Pack(overflow, 0, 0));
    }

    // Negative control for a wrong shift multiple: with (cx << PackBits) the X and Y axes collide.
    [Fact]
    public void TheThreeAxesArePackedIndependently()
    {
        var x = ChunkKey.Pack(1, 0, 0);
        var y = ChunkKey.Pack(0, 1, 0);
        var z = ChunkKey.Pack(0, 0, 1);

        Assert.NotEqual(x, y);
        Assert.NotEqual(y, z);
        Assert.NotEqual(x, z);

        Assert.Equal(1L << 42, x);
        Assert.Equal(1L << 21, y);
        Assert.Equal(1L, z);

        Assert.Equal(new Vector3(1, 0, 0), ChunkKey.ToVector3(x));
        Assert.Equal(new Vector3(0, 1, 0), ChunkKey.ToVector3(y));
        Assert.Equal(new Vector3(0, 0, 1), ChunkKey.ToVector3(z));
    }

    // A dense cube spanning both signs and well past WorldGenerationConfig.ChunkRadius: no two
    // distinct chunk positions may ever share a key. 81^3 = 531,441 keys.
    [Fact]
    public void NoTwoChunkPositionsInADenseSignedCubeShareAKey()
    {
        const int extent = 40;
        var keys = new HashSet<long>();
        var enumerated = 0;

        for (var cx = -extent; cx <= extent; cx++)
        for (var cy = -extent; cy <= extent; cy++)
        for (var cz = -extent; cz <= extent; cz++)
        {
            enumerated++;
            keys.Add(ChunkKey.Pack(cx, cy, cz));
        }

        Assert.Equal(531441, enumerated);
        Assert.Equal(enumerated, keys.Count);
    }

    // Negative control for the Vector3 overload disagreeing with the int one at all — an axis
    // swap or a wrong per-component conversion. It is explicitly NOT the truncation control:
    // every position reaching the chunk dictionaries is integral, and (int) and floor agree on
    // integral floats, so the negative-side floor-vs-truncate gap is closed by PackBlock's fact
    // below and by the replay probe, not by this one.
    [Fact]
    public void PackingAVectorAgreesWithPackingItsIntegerComponents()
    {
        foreach (var (x, y, z) in new[] { (0, 0, 0), (1, 2, 3), (-1, -2, -3), (-17, 0, 24), (17, -5, -24) })
        {
            Assert.Equal(ChunkKey.Pack(x, y, z), ChunkKey.Pack(new Vector3(x, y, z)));
        }
    }

    // THE FLOOR-VERSUS-TRUNCATE TRAP. A truncating PackBlock maps block -1 to chunk 0 instead of
    // chunk -1, which is exactly the defect C6 trapped for the placement buckets.
    [Fact]
    public void PackingABlockCellAgreesWithVoxelCoordsChunkPosOf()
    {
        foreach (var (x, y, z) in new[]
                 {
                     (0, 0, 0), (15, 15, 15), (16, 16, 16),
                     (-1, -1, -1), (-16, -16, -16), (-17, -17, -17),
                     (-1, 31, -33), (255, -1, 0)
                 })
        {
            Assert.Equal(ChunkKey.Pack(VoxelCoords.ChunkPosOf(x, y, z)), ChunkKey.PackBlock(x, y, z));
        }

        // Spelled out for the two cases truncation gets wrong, so the intent survives a refactor.
        Assert.Equal(ChunkKey.Pack(-1, -1, -1), ChunkKey.PackBlock(-1, -1, -1));
        Assert.Equal(ChunkKey.Pack(-1, -1, -1), ChunkKey.PackBlock(-16, -16, -16));
        Assert.NotEqual(ChunkKey.Pack(0, 0, 0), ChunkKey.PackBlock(-1, -1, -1));
    }

    [Fact]
    public void UnpackingToAVectorReturnsTheVectorThatWasPacked()
    {
        foreach (var v in new[]
                 {
                     new Vector3(0, 0, 0), new Vector3(1, 2, 3), new Vector3(-1, -2, -3),
                     new Vector3(-24, 1, 24), new Vector3(1000, -1000, 17)
                 })
        {
            Assert.Equal(v, ChunkKey.ToVector3(ChunkKey.Pack(v)));
        }
    }

    // CardinalOffsets exists only to stop the six neighbour literals being retyped per call site,
    // so it is worthless unless it stays in lockstep with CardinalDirections.
    [Fact]
    public void TheIntegerCardinalOffsetsMatchTheVectorCardinalDirections()
    {
        Assert.Equal(Vector3Directions.CardinalDirections.Length, Vector3Directions.CardinalOffsets.Length);
        Assert.Equal(6, Vector3Directions.CardinalOffsets.Length);

        for (var i = 0; i < Vector3Directions.CardinalDirections.Length; i++)
        {
            var d = Vector3Directions.CardinalDirections[i];
            var o = Vector3Directions.CardinalOffsets[i];

            Assert.Equal(MathUtils.FloorToInt(d.X), o.X);
            Assert.Equal(MathUtils.FloorToInt(d.Y), o.Y);
            Assert.Equal(MathUtils.FloorToInt(d.Z), o.Z);
        }
    }
}
