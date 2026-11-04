using System;
using System.Numerics;
using VoxelEngine.Effects.Sky;
using Xunit;

namespace VoxelEngine.Tests.Effects.Sky;

/// <summary>
/// The GL-free twin of the night dome's shooting stars (<see cref="ShootingStar"/>, the
/// <c>ShootingStar</c> term in sky-fragment.glsl): streaks are rare, deterministic, live inside their
/// slot, travel along a great circle through the upper sky, and shine only at their head's path.
/// </summary>
public sealed class ShootingStarTests
{
    private const float Px = 0.001f;

    private static int FirstSlotWithAStreak(out ShootingStar.Streak streak)
    {
        for (var slot = 0; slot < 400; slot++)
            if (ShootingStar.TryGetStreak(slot, out streak))
                return slot;
        throw new InvalidOperationException("no slot holds a streak");
    }

    [Fact]
    public void TheSlotsWrapWithTheHourlyStarClock()
    {
        Assert.Equal(0f, 3600f % SkyConfig.ShootingStarSlotSeconds);
        Assert.True(SkyConfig.ShootingStarLifeSeconds < SkyConfig.ShootingStarSlotSeconds);
    }

    [Fact]
    public void MostSlotsAreEmptyButSomeHoldAStreakAndTheChoiceIsStable()
    {
        var slots = (int)(3600f / SkyConfig.ShootingStarSlotSeconds);
        var held = 0;
        for (var slot = 0; slot < slots; slot++)
        {
            var first = ShootingStar.TryGetStreak(slot, out var a);
            Assert.Equal(first, ShootingStar.TryGetStreak(slot, out var b));
            Assert.Equal(a, b);
            if (first) held++;
        }
        Assert.InRange(held, 1, slots / 2);
    }

    [Fact]
    public void AStreakStartsHighLeavesItsSlotRoomAndRunsOnAGreatCircle()
    {
        for (var slot = 0; slot < 400; slot++)
        {
            if (!ShootingStar.TryGetStreak(slot, out var s)) continue;
            Assert.InRange(s.Start.Y, SkyConfig.ShootingStarMinSinElevation - 1e-4f, SkyConfig.ShootingStarMaxSinElevation + 1e-4f);
            Assert.Equal(1f, s.Start.Length(), 1e-4f);
            Assert.Equal(1f, s.Tangent.Length(), 1e-4f);
            Assert.Equal(0f, Vector3.Dot(s.Start, s.Tangent), 1e-4f);
            Assert.InRange(s.StartSeconds + SkyConfig.ShootingStarLifeSeconds, 0f, SkyConfig.ShootingStarSlotSeconds + 1e-4f);
        }
    }

    [Fact]
    public void TheHeadShinesAlongItsPathAndNowhereOffItOrOutsideItsLife()
    {
        var slot = FirstSlotWithAStreak(out var s);
        var slotStart = slot * SkyConfig.ShootingStarSlotSeconds;
        var mid = slotStart + s.StartSeconds + SkyConfig.ShootingStarLifeSeconds * 0.5f;
        var (head, _) = ShootingStar.Span(SkyConfig.ShootingStarLifeSeconds * 0.5f);
        var onPath = Vector3.Normalize(s.Start * MathF.Cos(head) + s.Tangent * MathF.Sin(head));

        Assert.True(ShootingStar.Light(onPath, mid, Px) > 0.1f);
        Assert.InRange(ShootingStar.Light(onPath, mid, Px), 0f, 1f);

        // Well to the side of the path, ahead of the head, and out of the life window: dark
        var side = Vector3.Normalize(onPath + s.Normal * (30f * Px));
        Assert.Equal(0f, ShootingStar.Light(side, mid, Px), 1e-3f);
        var ahead = Vector3.Normalize(s.Start * MathF.Cos(head + 0.2f) + s.Tangent * MathF.Sin(head + 0.2f));
        Assert.Equal(0f, ShootingStar.Light(ahead, mid, Px));
        Assert.Equal(0f, ShootingStar.Light(onPath, slotStart + s.StartSeconds + SkyConfig.ShootingStarLifeSeconds + 0.01f, Px));
    }

    [Fact]
    public void ThePathMovesForwardAndTheTailIsDimmerThanTheHead()
    {
        var slot = FirstSlotWithAStreak(out var s);
        var t = slot * SkyConfig.ShootingStarSlotSeconds + s.StartSeconds + SkyConfig.ShootingStarLifeSeconds * 0.6f;
        var (head, tail) = ShootingStar.Span(SkyConfig.ShootingStarLifeSeconds * 0.6f);
        Assert.True(head > tail);

        Vector3 At(float phi) => Vector3.Normalize(s.Start * MathF.Cos(phi) + s.Tangent * MathF.Sin(phi));
        var atHead = ShootingStar.Light(At(head - 1e-3f), t, Px);
        var midTail = ShootingStar.Light(At((head + tail) * 0.5f), t, Px);
        Assert.True(atHead > midTail && midTail > 0f);

        var (laterHead, _) = ShootingStar.Span(SkyConfig.ShootingStarLifeSeconds * 0.8f);
        Assert.True(laterHead > head);
    }
}
