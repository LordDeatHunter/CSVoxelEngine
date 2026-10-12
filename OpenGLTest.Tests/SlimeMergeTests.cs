using System.Linq;
using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The slime merge (<see cref="SlimeMerge"/>): two touching slimes of one size become one of the next
/// size once both are past the cooldown, never at the largest size, never while dying, and never
/// with a different size; a split's children do not merge straight back, and the merged slime keeps
/// the pair's share of health.
/// </summary>
public sealed class SlimeMergeTests
{
    public SlimeMergeTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    // Nobody in play: the slimes neither hop nor wander, so they stay where they are put
    private static readonly IMobTarget[] Nobody = [];

    private static EntityType SlimeType => EntityRegistry.Require("slime");

    private static ChunkManager StoneFloor() => MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);

    private static Vector3 Ground(float x, int size) => new(x, 1.02f + Slime.HalfExtentFor(size), 8.5f);

    private static void Run(SlimeManager manager, ChunkManager chunks, float seconds)
    {
        var drops = new DropManager();
        for (var t = 0f; t < seconds; t += Dt)
            manager.Update(Dt, chunks, drops, Nobody);
        Assert.Empty(drops.Drops);
    }

    [Fact]
    public void TwoTouchingSlimesOfOneSizeMergeOnceBothArePastTheCooldown()
    {
        var chunks = StoneFloor();
        var manager = new SlimeManager(SlimeType);
        var size = SlimeSize.Min;
        manager.Spawn(Ground(7.5f, size), size);
        manager.Spawn(Ground(7.5f + Slime.HalfExtentFor(size), size), size);

        Run(manager, chunks, WorldGenerationConfig.SlimeMergeCooldownSeconds - 0.5f);
        Assert.Equal(2, manager.Slimes.Count);

        Run(manager, chunks, 1f);
        var merged = Assert.Single(manager.Slimes);
        Assert.Equal(SlimeMerge.MergedSize(size), merged.Size);
        Assert.Equal(merged.MaxHealth, merged.Health);
        Assert.Equal(1, manager.LivingCount);
        Assert.False(Slime.IsColliding(merged.Position, merged.BodyHalfExtent, chunks));
    }

    [Fact]
    public void SlimesApartOfDifferentSizesDyingOrAtTheLargestSizeNeverMerge()
    {
        var chunks = StoneFloor();
        var cooldown = WorldGenerationConfig.SlimeMergeCooldownSeconds + 1f;

        var apart = new SlimeManager(SlimeType);
        apart.Spawn(Ground(3.5f, 1), 1);
        apart.Spawn(Ground(10.5f, 1), 1);
        Run(apart, chunks, cooldown);
        Assert.Equal(2, apart.Slimes.Count);

        var mixed = new SlimeManager(SlimeType);
        mixed.Spawn(Ground(7.5f, 1), 1);
        mixed.Spawn(Ground(7.5f, 2), 2);
        Run(mixed, chunks, cooldown);
        Assert.Equal(2, mixed.Slimes.Count);

        var giants = new SlimeManager(SlimeType);
        giants.Spawn(Ground(7.5f, SlimeSize.Max), SlimeSize.Max);
        giants.Spawn(Ground(8.5f, SlimeSize.Max), SlimeSize.Max);
        Run(giants, chunks, cooldown);
        Assert.Equal(2, giants.Slimes.Count);
        Assert.All(giants.Slimes, s => Assert.Equal(SlimeSize.Max, s.Size));

        // A dying slime is never merged; it just plays out its death
        var a = new Slime(SlimeType, Ground(7.5f, 1), 1);
        var b = new Slime(SlimeType, Ground(7.6f, 1), 1);
        for (var t = 0f; t < cooldown; t += Dt)
        {
            a.Update(Dt, chunks, null);
            b.Update(Dt, chunks, null);
        }
        Assert.True(SlimeMerge.CanMerge(a, b));
        b.BeginDying();
        Assert.False(SlimeMerge.CanMerge(a, b));
    }

    [Fact]
    public void ASplitsChildrenDoNotMergeStraightBack()
    {
        var chunks = StoneFloor();
        var manager = new SlimeManager(SlimeType);
        var parent = manager.Spawn(Ground(7.5f, 2), 2)!;
        // Across the half line while surviving: it splits on the next update
        parent.TakeDamage(parent.Health - parent.MaxHealth / 2);
        Run(manager, chunks, WorldGenerationConfig.SlimeMergeCooldownSeconds * 0.9f);

        var children = manager.Slimes.Where(s => s.Size == SlimeSize.ChildSize(2)).ToList();
        Assert.Equal(WorldGenerationConfig.SlimeSplitChildren, children.Count);
    }

    [Fact]
    public void AMergedSlimeKeepsThePairsShareOfHealth()
    {
        var mergedMax = SlimeSize.Health(SlimeType.MaxHealth, 2);
        var smallMax = SlimeSize.Health(SlimeType.MaxHealth, 1);
        Assert.Equal(mergedMax, SlimeMerge.MergedHealth(smallMax, smallMax, smallMax, smallMax, mergedMax));

        // More health in never gives less health out, and the result stays inside the pool
        var previous = 0;
        for (var a = 0; a <= smallMax; a++)
        {
            var health = SlimeMerge.MergedHealth(a, smallMax, smallMax / 2, smallMax, mergedMax);
            Assert.InRange(health, 1, mergedMax);
            Assert.True(health >= previous);
            previous = health;
        }

        // A wounded pair makes a wounded slime
        Assert.True(SlimeMerge.MergedHealth(smallMax / 2, smallMax, smallMax / 2, smallMax, mergedMax) < mergedMax);
    }
}
