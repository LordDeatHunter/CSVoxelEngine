using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.StatusEffects;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Crawlers;
using VoxelEngine.Status;
using Xunit;

namespace VoxelEngine.Tests.Entities.Crawlers;

/// <summary>
/// How a crawler gets past what its straight crawl cannot reach, driven headless: a crawler on a wall level with a
/// target across the room (where the crawl alone only hovers at the target's height) leaps at it when it is in
/// leaping range and finds its way down, across and up when it is not; every step of a planned path is a move a
/// crawler makes; and a leap's arc comes down on the point it was aimed at.
/// </summary>
public sealed class CrawlerNavigationTests
{
    public CrawlerNavigationTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    // The room: a stone floor at y = 0, a wall over x = 0..1 up to WallTop, and a platform PlatformWidth wide from a
    // given x, solid up to PlatformTop
    private const int WallTop = 14;
    private const int PlatformTop = 6;
    private const int PlatformWidth = 4;
    private const float SpiderHalf = SpiderConfig.SpiderBodySize * 0.5f;

    private static ChunkManager WallAndPlatform(int chunksX, int platformX)
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var chunks = new ChunkManager(null!);
        for (var cx = 0; cx < chunksX; cx++)
        {
            var chunk = new Chunk(null!, new Vector3(cx, 0f, 0f), generateBlockData: false) { IsBlockDataReady = true };
            chunks.Chunks[new Vector3(cx, 0f, 0f)] = chunk;
            for (var lx = 0; lx < Chunk.Size; lx++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var x = cx * Chunk.Size + lx;
                var top = x <= 1 ? WallTop : x >= platformX && x < platformX + PlatformWidth ? PlatformTop : 0;
                for (var y = 0; y <= top; y++)
                    chunk.SetBlockRaw(lx, y, z, stone, 0);
            }
        }
        return chunks;
    }

    private static Spider SpiderOnTheWall() =>
        new(EntityRegistry.Require(Spider.EntityName), new Vector3(2f + SpiderHalf, 7.5f, 8.5f), tier: 0,
            broodmother: false, spawnIn: false);

    private static StubTarget OnThePlatform(int platformX) =>
        new(StubTarget.EyeAt(new Vector3(platformX + 1.5f, PlatformTop + 1f, 8.5f))) { Effects = new StatusEffectSet() };

    [Fact]
    public void AStuckCrawlerLeapsAtATargetInLeapingRange()
    {
        const int platformX = 7;
        var chunks = WallAndPlatform(1, platformX);
        var spider = SpiderOnTheWall();
        var target = OnThePlatform(platformX);

        spider.Update(Dt, chunks, target);
        Assert.True(spider.IsClinging, "the spider did not take hold of the wall");
        var leapt = false;
        for (var i = 0; i < (int)(8f / Dt) && !spider.InBiteReach(spider.Position, target.Eye); i++)
        {
            spider.Update(Dt, chunks, target);
            leapt |= !spider.IsClinging;
        }

        Assert.True(leapt, "the spider never left the wall");
        Assert.True(spider.InBiteReach(spider.Position, target.Eye), "the spider never reached the target");
    }

    [Fact]
    public void AStuckCrawlerOutOfLeapingRangeFindsItsWayDownAcrossAndUp()
    {
        const int platformX = 14;
        var chunks = WallAndPlatform(2, platformX);
        var spider = SpiderOnTheWall();
        var target = OnThePlatform(platformX);

        for (var i = 0; i < (int)(20f / Dt) && !spider.InBiteReach(spider.Position, target.Eye); i++)
            spider.Update(Dt, chunks, target);

        Assert.True(spider.InBiteReach(spider.Position, target.Eye), "the spider never found its way to the target");
        Assert.True(spider.Position.Y > PlatformTop + 1f, "the spider is not up on the platform");
    }

    [Fact]
    public void EveryStepOfAPathIsAMoveACrawlerMakes()
    {
        const int platformX = 14;
        var chunks = WallAndPlatform(2, platformX);
        var start = new CrawlerFoothold(2, 7, 8, CrawlFrame.IndexOf(Vector3.UnitX));
        var chest = RealmMob.ChestOf(OnThePlatform(platformX).Eye);
        var path = new List<CrawlerFoothold>();

        Assert.True(CrawlerPathfinder.Shared.TryFind(start, chest, SpiderConfig.SpiderBiteReach, SpiderHalf, chunks, path));
        Assert.Equal(start, path[0]);
        Assert.True(Vector3.Distance(path[^1].Center(SpiderHalf), chest) <= SpiderConfig.SpiderBiteReach);

        var moves = new List<(CrawlerFoothold Foothold, float Cost)>();
        for (var i = 0; i < path.Count; i++)
        {
            var step = path[i];
            var (nx, ny, nz) = ((int)step.Normal.X, (int)step.Normal.Y, (int)step.Normal.Z);
            Assert.False(BlockType.IsSolid(chunks.GetBlockGlobal(step.X, step.Y, step.Z)), $"step {i} is inside rock");
            Assert.True(BlockType.IsSolid(chunks.GetBlockGlobal(step.X - nx, step.Y - ny, step.Z - nz)),
                $"step {i} holds onto nothing");
            if (i == 0) continue;

            moves.Clear();
            CrawlerPathfinder.Moves(path[i - 1], SpiderHalf, chunks, moves);
            Assert.Contains(moves, move => move.Foothold == step);
        }
    }

    [Theory]
    [InlineData(5f, 0f, 0f)]
    [InlineData(1.5f, 2f, 1f)]
    [InlineData(4f, -3f, -2f)]
    [InlineData(0.5f, 0.5f, 3f)]
    public void ALeapComesDownOnTheSpotItWasAimedAt(float dx, float dy, float dz)
    {
        var from = new Vector3(3f, 5f, 3f);
        var to = from + new Vector3(dx, dy, dz);
        var velocity = SurfaceCrawler.LeapVelocity(from, to, CrawlerConfig.LeapHorizontalSpeed);

        // Find the flight time from the horizontal travel (the arc is a parabola under the loose-body gravity)
        var flat = new Vector2(dx, dz).Length();
        var horizontalSpeed = new Vector2(velocity.X, velocity.Z).Length();
        Assert.True(horizontalSpeed > 0f);
        var time = flat / horizontalSpeed;
        var landing = from + velocity * time - Vector3.UnitY * (0.5f * BodyMotion.Gravity * time * time);
        Assert.True(Vector3.Distance(landing, to) < 1e-3f, $"the leap landed {Vector3.Distance(landing, to)} off");
        // It is on its way down when it gets there, so it lands rather than sails past
        Assert.True(velocity.Y - BodyMotion.Gravity * time < 0f);
    }
}
