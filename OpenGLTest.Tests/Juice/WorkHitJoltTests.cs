using System;
using VoxelEngine.Juice;
using Xunit;

namespace VoxelEngine.Tests.Juice;

/// <summary>
/// The jolt a tool's hit gives the pile on a work station (<see cref="WorkHitJolt"/>): the pieces leave the surface
/// and come back to exactly where they lay, never sinking into it, the upper ones jumping further, and a pile that
/// was not hit lies still.
/// </summary>
public sealed class WorkHitJoltTests
{
    [Fact]
    public void APileThatWasNotHitOrWhoseJoltIsOverLiesStill()
    {
        foreach (var elapsed in new[] { float.PositiveInfinity, float.NaN, -0.1f, WorkHitJolt.Seconds, WorkHitJolt.Seconds * 3f })
            for (var piece = 0; piece < 4; piece++)
                Assert.Equal(default, WorkHitJolt.At(elapsed, piece));
    }

    [Fact]
    public void EveryPieceJumpsAndNeverSinksBelowWhereItRests()
    {
        for (var piece = 0; piece < 6; piece++)
        {
            var highest = 0f;
            for (var t = 0f; t < WorkHitJolt.Seconds; t += WorkHitJolt.Seconds / 120f)
            {
                var pose = WorkHitJolt.At(t, piece);
                Assert.True(pose.Lift >= 0f, $"piece {piece} sank at {t}");
                Assert.InRange(pose.Spread, 0f, 0.99f);
                highest = MathF.Max(highest, pose.Lift);
            }
            Assert.True(highest > 0f, $"piece {piece} never left the surface");
        }
    }

    [Fact]
    public void APieceHigherInThePileJumpsFurtherSoThePileNeverInterpenetrates()
    {
        for (var t = 0f; t < WorkHitJolt.Seconds; t += WorkHitJolt.Seconds / 120f)
            for (var piece = 1; piece < 6; piece++)
                Assert.True(WorkHitJolt.At(t, piece).Lift >= WorkHitJolt.At(t, piece - 1).Lift);
    }

    [Fact]
    public void TheJoltEndsWhereItBegan()
    {
        var last = WorkHitJolt.At(WorkHitJolt.Seconds * 0.999f, 3);
        Assert.InRange(last.Lift, 0f, 1e-3f);
        Assert.InRange(MathF.Abs(last.Twist), 0f, 1e-3f);
        Assert.Equal(0f, last.Spread, 3);
    }
}
