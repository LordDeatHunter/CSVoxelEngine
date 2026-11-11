using System;
using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Sandworms;
using VoxelEngine.Entities.Slimes;
using Xunit;

namespace VoxelEngine.Tests.Entities;

/// <summary>
/// The shared mob-to-mob collision (<see cref="MobSeparation"/>): two bodies in one cell are pushed
/// apart along the cheaper axis, a wall keeps one of them still while the other takes the whole push,
/// and a pair that only meets at different heights, a dying body and the boss are left alone.
/// </summary>
public sealed class MobSeparationTests
{
    public MobSeparationTests() => TestContent.EnsureLoaded();

    private static ChunkManager Floor() => MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);

    private static Slime SlimeAt(float x, float z, float feetY = 1f) =>
        new(EntityRegistry.Require("slime"), new Vector3(x, feetY + Slime.HalfExtent, z));

    // Overlap beyond the hair MobSeparation deliberately leaves for slime merging
    private static bool Overlapping(Mob a, Mob b)
    {
        var allowed = MobSeparation.TouchOverlap + 0.0001f;
        return MathF.Abs(a.Position.X - b.Position.X) < a.BodyHalfExtent + b.BodyHalfExtent - allowed
               && MathF.Abs(a.Position.Z - b.Position.Z) < a.BodyHalfExtent + b.BodyHalfExtent - allowed
               && MathF.Abs(a.Position.Y - b.Position.Y) < a.BodyHalfHeight + b.BodyHalfHeight - allowed;
    }

    [Fact]
    public void TwoBodiesInTheSameSpotArePushedApartWithoutLeavingTheFloor()
    {
        var chunks = Floor();
        var a = SlimeAt(8.5f, 8.5f);
        var b = SlimeAt(8.5f, 8.5f);

        MobSeparation.Separate([a, b], chunks);

        Assert.False(Overlapping(a, b));
        Assert.Equal(1f, a.Position.Y - a.BodyHalfHeight, 3);
        Assert.Equal(1f, b.Position.Y - b.BodyHalfHeight, 3);
    }

    [Fact]
    public void SeparatedSlimesStillCountAsTouchingForTheirMerge()
    {
        var chunks = Floor();
        var a = SlimeAt(8.5f, 8.5f);
        var b = SlimeAt(8.5f, 8.5f);
        a.AnimateMirror(MobConfig.SlimeMergeCooldownSeconds);
        b.AnimateMirror(MobConfig.SlimeMergeCooldownSeconds);

        MobSeparation.Separate([a, b], chunks);

        Assert.False(Overlapping(a, b));
        Assert.True(SlimeMerge.CanMerge(a, b), "separation left the pair too far apart to merge");
    }

    [Fact]
    public void AWallKeepsOneBodyStillAndTheOtherTakesTheWholePush()
    {
        var chunks = Floor();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var y = 1; y <= 3; y++)
        for (var z = 0; z < Chunk.Size; z++)
            chunks.Chunks[Vector3.Zero].SetBlockRaw(5, y, z, stone, 0);

        var a = SlimeAt(4.4f, 8.5f);
        var b = SlimeAt(4.6f, 8.5f);

        MobSeparation.Separate([a, b], chunks);

        Assert.False(Overlapping(a, b));
        Assert.True(a.Position.X < b.Position.X, "the pair was pushed into the wall");
        Assert.True(a.Position.X + a.BodyHalfExtent <= 5.01f);
        Assert.True(b.Position.X + b.BodyHalfExtent <= 5.01f);
    }

    [Fact]
    public void BodiesThatOnlyMeetAtDifferentHeightsAndTheDyingAndTheBossAreLeftAlone()
    {
        var chunks = Floor();
        var low = SlimeAt(8.5f, 8.5f);
        var high = SlimeAt(8.5f, 8.5f, feetY: 3f);
        var lowAt = low.Position;
        var highAt = high.Position;
        MobSeparation.Separate([low, high], chunks);
        Assert.Equal(lowAt, low.Position);
        Assert.Equal(highAt, high.Position);

        var dying = SlimeAt(8.5f, 8.5f);
        dying.BeginDying();
        var dyingAt = dying.Position;
        var livingAt = low.Position;
        MobSeparation.Separate([low, dying], chunks);
        Assert.Equal(dyingAt, dying.Position);
        Assert.Equal(livingAt, low.Position);

        var wormType = EntityRegistry.Require(Sandworm.EntityName);
        var worm = new Sandworm(wormType, low.Position);
        var wormAt = worm.Position;
        MobSeparation.Separate([low, worm], chunks);
        Assert.Equal(wormAt, worm.Position);
        Assert.Equal(livingAt, low.Position);
    }
}
