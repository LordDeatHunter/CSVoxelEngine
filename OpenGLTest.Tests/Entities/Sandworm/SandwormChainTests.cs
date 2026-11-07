using System;
using System.Numerics;
using VoxelEngine.Entities.Sandworm;
using Xunit;

namespace VoxelEngine.Tests.Entities.Sandworm;

public class SandwormChainTests
{
    private const float Eps = 1e-3f;

    private static float Bend(SandwormChain chain, int i) =>
        MathF.Acos(Math.Clamp(Vector3.Dot(
            Vector3.Normalize(chain[i - 1] - chain[i - 2]),
            Vector3.Normalize(chain[i] - chain[i - 1])), -1f, 1f));

    [Fact]
    public void EveryLinkKeepsItsLengthAlongAnyHeadPath()
    {
        var chain = new SandwormChain(new Vector3(0, 60, 0), -Vector3.UnitZ);
        var rng = new Random(7);
        var head = chain.Head;
        for (var step = 0; step < 400; step++)
        {
            head += new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f) * 3f;
            chain.Follow(head);
            for (var i = 1; i < chain.Count; i++)
                Assert.InRange(Vector3.Distance(chain[i], chain[i - 1]), SandwormChain.LinkLength(i) - Eps, SandwormChain.LinkLength(i) + Eps);
        }
    }

    [Fact]
    public void MovingTheHeadTugsEverySegmentAlong()
    {
        var chain = new SandwormChain(Vector3.Zero, Vector3.UnitX);
        var before = new Vector3[chain.Count];
        for (var i = 0; i < chain.Count; i++) before[i] = chain[i];

        // Pull the head far away along its own heading, so the whole body must follow.
        var head = chain.Head;
        for (var step = 0; step < 40; step++)
        {
            head += Vector3.UnitX * 2f;
            chain.Follow(head);
        }

        for (var i = 1; i < chain.Count; i++)
            Assert.True(Vector3.Distance(chain[i], before[i]) > SandwormChain.LinkLength(i), $"segment {i} did not follow");
    }

    [Fact]
    public void ASharpTurnNeverBendsALinkPastTheLimit()
    {
        var chain = new SandwormChain(Vector3.Zero, Vector3.UnitX);
        var head = chain.Head;
        for (var step = 0; step < 30; step++)
        {
            head += Vector3.UnitX * 2f;
            chain.Follow(head);
        }

        // A right-angle turn of the head: the body must sweep round, each link within the limit.
        for (var step = 0; step < 60; step++)
        {
            head += Vector3.UnitZ * 2f;
            chain.Follow(head);
            for (var i = 2; i < chain.Count; i++)
                Assert.True(Bend(chain, i) <= SandwormConfig.MaxBendDegrees * MathF.PI / 180f + Eps);
        }
    }

    [Fact]
    public void TheBodyTrailsBehindTheHeadAfterAStraightRun()
    {
        var chain = new SandwormChain(Vector3.Zero, Vector3.UnitX);
        var head = chain.Head;
        for (var step = 0; step < 80; step++)
        {
            head += Vector3.UnitX * 2f;
            chain.Follow(head);
        }

        for (var i = 1; i < chain.Count; i++)
            Assert.True(chain[i].X < chain[i - 1].X, $"segment {i} is not behind segment {i - 1}");
        Assert.True(Vector3.Dot(chain.Forward(0), Vector3.UnitX) > 0.99f);
    }

    [Fact]
    public void TheTaperNeverGrowsAndTheEndsAreTheHeadAndTail()
    {
        Assert.Equal(SandwormSegmentKind.Head, SandwormChain.KindOf(0));
        Assert.Equal(SandwormSegmentKind.Tail, SandwormChain.KindOf(SandwormConfig.SegmentCount - 1));
        Assert.Equal(SandwormSegmentKind.Body, SandwormChain.KindOf(1));
        for (var i = 1; i < SandwormConfig.SegmentCount; i++)
            Assert.True(SandwormChain.ScaleOf(i) <= SandwormChain.ScaleOf(i - 1) + 1e-6f);
    }
}
