using System;
using System.Collections.Generic;
using VoxelEngine.Audio;
using VoxelEngine.Entities.TowerMobs;
using Xunit;

namespace VoxelEngine.Tests.Entities.TowerMobs;

public sealed class SkeletonRattleTests
{
    private const float Step = 0.5f;

    private static int CountPlays(float seconds, bool nearby, List<string>? groups = null)
    {
        var played = groups ?? [];
        var rattle = new SkeletonRattle(played.Add, new Random(7));
        for (var t = 0f; t < seconds; t += Step)
            rattle.Update(Step, nearby);
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
    public void ShippedClipsFormTheRattleGroup()
    {
        var groups = SfxLibrary.Scan(System.IO.Path.Combine(TestContent.FindAssetsRoot(), "sfx"));
        Assert.True(groups.TryGetValue(SfxLibrary.SkeletonRattleGroup, out var clips));
        Assert.True(clips!.Count > 1);
    }
}
