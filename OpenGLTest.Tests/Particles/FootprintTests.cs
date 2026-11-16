using System.Numerics;
using VoxelEngine.Audio;
using VoxelEngine.Content;
using VoxelEngine.Particles.Footprints;
using VoxelEngine.Tests.Support;
using Xunit;

namespace VoxelEngine.Tests.Particles;

public sealed class FootprintTests
{
    public FootprintTests() => TestContent.EnsureLoaded();

    [Fact]
    public void APrintHoldsThenFadesToNothingAtTheEndOfItsPersistence()
    {
        const float visibility = 0.5f, persistence = 20f;
        Assert.Equal(visibility, FootprintRules.Alpha(visibility, persistence, 0f));
        var last = visibility;
        for (var age = 0f; age < persistence; age += 0.5f)
        {
            var alpha = FootprintRules.Alpha(visibility, persistence, age);
            Assert.InRange(alpha, 0f, last);
            last = alpha;
        }
        Assert.Equal(0f, FootprintRules.Alpha(visibility, persistence, persistence));
        Assert.Equal(0f, FootprintRules.Alpha(visibility, 0f, 0f));
    }

    [Fact]
    public void ASofterTierShowsMoreAndLastsLongerThanTheHardDefault()
    {
        var air = BlockType.Air;
        BlockType.FootprintOf(air, out var hardVisibility, out var hardPersistence);
        foreach (var (soft, softest) in new[] { ("dirt", "sand"), ("grass", "snow") })
        {
            BlockType.FootprintOf(ContentRegistry.RequireBlock(soft).Id, out var softVisibility, out var softPersistence);
            BlockType.FootprintOf(ContentRegistry.RequireBlock(softest).Id, out var topVisibility, out var topPersistence);
            Assert.True(softVisibility > hardVisibility && softPersistence > hardPersistence, soft);
            Assert.True(topVisibility >= softVisibility && topPersistence > softPersistence, softest);
        }
    }

    [Fact]
    public void TheFeetSitBesideTheWalkingLineAndMirrorEachOther()
    {
        var heading = new Vector2(0f, -1f);
        var left = new Vector3[4];
        var right = new Vector3[4];
        FootprintRules.Corners(Vector3.Zero, heading, -1, left);
        FootprintRules.Corners(Vector3.Zero, heading, 1, right);
        float Center(Vector3[] c) => (c[0].X + c[1].X + c[2].X + c[3].X) / 4f;
        Assert.True(Center(left) < 0f && Center(right) > 0f);
        Assert.Equal(-Center(left), Center(right), 4);
        Assert.All(left, corner => Assert.True(corner.Y > 0f));
    }

    [Fact]
    public void TheOldestPrintGoesWhenTheCapIsReached()
    {
        var system = new FootprintSystem();
        var id = ContentRegistry.RequireBlock("dirt").Id;
        for (var i = 0; i < FootprintConfig.Max + 5; i++)
            system.Spawn(new FootfallEvent(new Vector3(i, 0f, 0f), new Vector2(0f, -1f), Vector3.Zero, id, 1));
        Assert.Equal(FootprintConfig.Max, system.Prints.Count);
        Assert.Equal(5f, system.Prints[0].Feet.X);
    }
}
