using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Audio;
using VoxelEngine.Entities;
using VoxelEngine.Entities.TowerMobs;
using Xunit;

namespace VoxelEngine.Tests.Entities.TowerMobs;

public sealed class SkeletonRattleTests
{
    private const float Step = 0.5f;

    public SkeletonRattleTests() => TestContent.EnsureLoaded();

    private static int CountPlays(float seconds, bool nearby, List<string>? groups = null)
    {
        var played = groups ?? [];
        var rattle = new SkeletonRattle((group, _) => played.Add(group), new Random(7));
        for (var t = 0f; t < seconds; t += Step)
            rattle.Update(Step, nearby ? Vector3.Zero : null);
        return played.Count;
    }

    [Fact]
    public void RattlesNeverWhenNoSkeletonIsNearby() =>
        Assert.Equal(0, CountPlays(600f, nearby: false));

    [Fact]
    public void RattlesAreOccasionalNotConstant()
    {
        var plays = CountPlays(600f, nearby: true);
        Assert.InRange(plays, 1, (int)(600f / AudioConfig.SkeletonRattleMinSeconds));
    }

    [Fact]
    public void RattlePlaysTheSkeletonGroup()
    {
        var groups = new List<string>();
        CountPlays(600f, nearby: true, groups);
        Assert.All(groups, g => Assert.Equal(SfxLibrary.SkeletonRattleGroup, g));
    }

    [Fact]
    public void TheRattleComesFromTheNearestLiveSkeletonInEarshot()
    {
        var type = EntityRegistry.Require(Skeleton.EntityName);
        var listener = new Vector3(0f, 2f, 0f);
        var near = new Skeleton(type, new Vector3(6f, 2f, 0f), spawnIn: false);
        var nearer = new Skeleton(type, new Vector3(0f, 2f, 3f), spawnIn: false);
        var dead = new Skeleton(type, new Vector3(1f, 2f, 0f), spawnIn: false);
        dead.TakeDamage(dead.Health + 100);
        var far = new Skeleton(type, new Vector3(AudioConfig.SkeletonRattleRangeBlocks + 5f, 2f, 0f), spawnIn: false);

        Assert.Equal(nearer.Position, SkeletonRattle.NearestWithin([near, dead, nearer, far], listener,
            AudioConfig.SkeletonRattleRangeBlocks));
        Assert.Null(SkeletonRattle.NearestWithin([dead, far], listener, AudioConfig.SkeletonRattleRangeBlocks));
    }

    [Fact]
    public void ShippedClipsFormTheRattleGroup()
    {
        var groups = SfxLibrary.Scan(System.IO.Path.Combine(TestContent.FindAssetsRoot(), "sfx"));
        Assert.True(groups.TryGetValue(SfxLibrary.SkeletonRattleGroup, out var clips));
        Assert.True(clips!.Count > 1);
    }
}
