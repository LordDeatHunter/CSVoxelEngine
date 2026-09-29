using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class GoblinAimTests
{
    private const float Speed = WorldGenerationConfig.GoblinStoneSpeed;
    private const float Gravity = ThrownProjectile.Gravity;

    private static readonly Vector3 Origin = new(3f, 70f, -5f);

    // Targets above, below and level, near and far, in different compass directions; all
    // within reach of the stone speed.
    public static TheoryData<float, float, float> Targets => new()
    {
        { 4f, 0f, 0f },
        { 0f, 1.5f, -7f },
        { -6f, -3f, 6f },
        { 8f, 2.5f, 5f },
        { -11f, 0.6f, -2f },
        { 2f, -8f, 1f },
    };

    private static Vector3 PositionAtTarget(Vector3 from, Vector3 to, Vector3 velocity)
    {
        var horizontal = new Vector2(to.X - from.X, to.Z - from.Z).Length();
        var t = horizontal / new Vector2(velocity.X, velocity.Z).Length();
        return from + velocity * t - 0.5f * Gravity * t * t * Vector3.UnitY;
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void SolvedArcLandsOnTheTargetUnderTheSameGravity(float dx, float dy, float dz)
    {
        var to = Origin + new Vector3(dx, dy, dz);

        Assert.True(GoblinAim.TryLaunchVelocity(Origin, to, Speed, Gravity, out var velocity));

        Assert.True(Vector3.Distance(PositionAtTarget(Origin, to, velocity), to) < 0.05f);
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void SolvedVelocityHasTheRequestedSpeed(float dx, float dy, float dz)
    {
        Assert.True(GoblinAim.TryLaunchVelocity(Origin, Origin + new Vector3(dx, dy, dz), Speed, Gravity, out var velocity));

        Assert.Equal(Speed, velocity.Length(), 3);
    }

    [Fact]
    public void UnreachableTargetStillTossesTowardIt()
    {
        // Three times the level range v^2/g is out of reach at any angle.
        var reach = Speed * Speed / Gravity;
        var direction = Vector3.Normalize(new Vector3(-2f, 0f, 1f));
        var to = Origin + direction * (3f * reach) + Vector3.UnitY;

        Assert.False(GoblinAim.TryLaunchVelocity(Origin, to, Speed, Gravity, out var velocity));

        Assert.Equal(Speed, velocity.Length(), 3);
        var horizontal = Vector3.Normalize(new Vector3(velocity.X, 0f, velocity.Z));
        Assert.True(Vector3.Dot(horizontal, direction) > 0.999f);
        Assert.True(velocity.Y > 0f);
    }

    [Fact]
    public void TheWholeThrowBandIsSolvableAtStoneSpeed()
    {
        // The goblin only throws inside [GoblinThrowMinRange, GoblinThrowMaxRange], at a chest a
        // little above its hand; the band's far edge must stay within the stone's reach.
        var to = Origin + new Vector3(WorldGenerationConfig.GoblinThrowMaxRange, 0.6f, 0f);

        Assert.True(GoblinAim.TryLaunchVelocity(Origin, to, Speed, Gravity, out _));
    }
}
