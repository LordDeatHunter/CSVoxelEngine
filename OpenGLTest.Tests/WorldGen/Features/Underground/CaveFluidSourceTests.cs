using System;
using System.Collections.Generic;
using VoxelEngine.WorldGen.Features.Underground;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Features.Underground;

public sealed class CaveFluidSourceTests
{
    [Fact]
    public void GroupsAreOneOrTwoAdjacentCandidatesWithWideGaps()
    {
        var groups = new List<(int X, int Y, int Z)>();
        for (var cx = -2; cx <= 2; cx++)
        for (var cy = -3; cy <= -1; cy++)
        for (var cz = -2; cz <= 2; cz++)
        {
            var site = CaveFluidSources.SiteAt(cx, cy, cz);
            Assert.True(CaveFluidSources.Contains(site.X, site.Y, site.Z));
            Assert.Equal(site.Pair, CaveFluidSources.Contains(site.X + 1, site.Y, site.Z));
            Assert.False(CaveFluidSources.Contains(site.X - 1, site.Y, site.Z));
            Assert.False(CaveFluidSources.Contains(site.X + 2, site.Y, site.Z));
            Assert.False(CaveFluidSources.Contains(site.X, site.Y + 1, site.Z));
            foreach (var previous in groups)
            {
                var distance = Math.Max(Math.Abs(site.X - previous.X),
                    Math.Max(Math.Abs(site.Y - previous.Y), Math.Abs(site.Z - previous.Z)));
                Assert.True(distance >= 32, "independent groups became adjacent fluid carpets");
            }
            groups.Add((site.X, site.Y, site.Z));
        }
    }
}
