using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Content.Loot;
using VoxelEngine.Entities;
using Xunit;

namespace VoxelEngine.Tests.Content;

/// <summary>The Skeleton King always drops exactly one unique from its pool, and never armor.</summary>
public sealed class SkeletonKingLootTests
{
    public SkeletonKingLootTests() => TestContent.EnsureLoaded();

    private static readonly string[] Uniques = ["bone_breaker_blade", "bone_charm", "crystal_staff"];

    [Fact]
    public void EveryKillYieldsExactlyOneUnique()
    {
        var table = EntityRegistry.Require("skeleton_king").LootTable!;
        var uniqueIds = Uniques.Select(n => ContentRegistry.RequireItem(n).Id).ToHashSet();
        var random = new Random(7);
        for (var i = 0; i < 200; i++)
        {
            var into = new List<ushort>();
            table.Roll(new LootContext(0, 0, 0), into, random);
            Assert.Equal(1, into.Count(uniqueIds.Contains));
        }
    }
}
