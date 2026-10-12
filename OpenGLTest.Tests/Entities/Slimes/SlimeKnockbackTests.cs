using System;
using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Size-scaled knockback (<see cref="SlimeSize.ShoveSpeed"/>): the same shove pushes a larger slime
/// less, and the smallest slime takes it as given.
/// </summary>
public sealed class SlimeKnockbackTests
{
    public SlimeKnockbackTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    [Fact]
    public void TheShoveSpeedFallsAsTheSizeGrows()
    {
        const float shove = 5f;
        Assert.Equal(shove, SlimeSize.ShoveSpeed(shove, SlimeSize.Min), 5);
        for (var size = SlimeSize.Min; size < SlimeSize.Max; size++)
        {
            var smaller = SlimeSize.ShoveSpeed(shove, size);
            var larger = SlimeSize.ShoveSpeed(shove, size + 1);
            Assert.True(larger > 0f, $"a size {size + 1} slime is not shoved at all");
            Assert.True(larger < smaller, $"size {size + 1} is shoved {larger}, size {size} {smaller}");
        }
    }

    [Fact]
    public void TheSamePunchSlidesALargerSlimeAShorterWay()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var type = EntityRegistry.Require("slime");
        var previous = float.PositiveInfinity;
        for (var size = SlimeSize.Min; size <= SlimeSize.Max; size++)
        {
            // A hair above the floor, so float rounding never starts the body inside it
            var start = new Vector3(4.5f, 1.05f + Slime.HalfExtentFor(size), 8.5f);
            var slime = new Slime(type, start, size);
            slime.Shove(Vector3.UnitX, WorldGenerationConfig.SlimePunchShoveSpeed);
            Assert.True(slime.Velocity.X > 0f);

            // Nobody to chase, so the slime never hops: friction alone stops the slide
            for (var i = 0; i < 60; i++)
                slime.Update(Dt, chunks, null);

            var slid = slime.Position.X - start.X;
            Assert.True(slid > 0f, $"a size {size} slime did not move");
            Assert.True(slid < previous, $"a size {size} slime slid {slid}, farther than the smaller one's {previous}");
            previous = slid;
        }
    }
}
