using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Entities.Crawlers;

/// <summary>
/// Steers a chasing crawler past what its straight crawl cannot reach. The crawl aims at the target's chest along
/// whatever face it holds, which leaves it hovering level with a target off that face (a crawler on a wall beside a
/// player across the room) or pressed into a dead end. The navigator watches the chase close in; once it has
/// stalled for <see cref="CrawlerConfig.StallSeconds"/> it says so (the mob then leaps when it can) and, asked to,
/// plans a path over the block faces (<see cref="CrawlerPathfinder"/>) whose footholds it hands out one at a time as
/// the crawl's aim, until the path ends, goes stale or stops making progress. GL-free; one per crawler, main thread
/// only.
/// </summary>
public sealed class CrawlerNavigator
{
    // How many footholds past the next one a crawler may be found on (a wrap can skip one)
    private const int LookAhead = 4;

    private readonly List<CrawlerFoothold> _path = [];
    private int _next;
    private Vector3 _plannedFor;
    private float _pathIdle;
    private float _planCooldown;
    private float _best = float.PositiveInfinity;
    private float _stall;

    /// <summary>Whether a planned path is being followed.</summary>
    public bool Following => _path.Count > 0;

    /// <summary>Drops the path and the progress record: the chase is over or on hold.</summary>
    public void Reset()
    {
        _path.Clear();
        _best = float.PositiveInfinity;
        _stall = 0f;
    }

    /// <summary>
    /// The point the crawl aims at this frame, for a body of <paramref name="halfExtent"/> at
    /// <paramref name="position"/> chasing a target whose chest is at <paramref name="chest"/>: the chest itself, or
    /// the next foothold of the path being followed. <paramref name="stalled"/> turns true, once per stall, when
    /// the chase stopped closing in or the path was lost; <paramref name="letGo"/> when the path's next step is a
    /// fall from the foothold the crawler holds.
    /// </summary>
    public Vector3 Aim(Vector3 position, float halfExtent, Vector3 chest, SurfaceCrawler crawler, float deltaTime,
        out bool stalled, out bool letGo)
    {
        stalled = false;
        letGo = false;
        _planCooldown = MathF.Max(0f, _planCooldown - deltaTime);
        var distance = Vector3.Distance(position, chest);

        if (_path.Count > 0)
        {
            var state = Follow(position, halfExtent, chest, crawler, deltaTime, out var aim, out letGo);
            if (state == PathState.Following) return aim;
            _path.Clear();
            Restart(distance);
            stalled = state == PathState.Lost;
            return chest;
        }

        // Closer by a real step (the first frame always is): the chase is going somewhere
        if (distance < _best - CrawlerConfig.StallProgressBlocks)
        {
            Restart(distance);
            return chest;
        }

        _stall += deltaTime;
        if (_stall < CrawlerConfig.StallSeconds) return chest;
        Restart(distance);
        stalled = true;
        return chest;
    }

    /// <summary>
    /// Plans a path from the foothold the crawler holds toward <paramref name="chest"/>, ending within
    /// <paramref name="arriveRadius"/> of it where it can, and starts following it. At most one plan every
    /// <see cref="CrawlerConfig.PathRetrySeconds"/>, and only for a crawler holding a surface; false when none was
    /// made or none was found.
    /// </summary>
    public bool Plan(Vector3 position, float halfExtent, Vector3 chest, float arriveRadius, SurfaceCrawler crawler,
        ChunkManager chunks)
    {
        if (_planCooldown > 0f || !crawler.Attached) return false;
        _planCooldown = CrawlerConfig.PathRetrySeconds;

        var (x, y, z) = position.BlockCell();
        var start = new CrawlerFoothold(x, y, z, CrawlFrame.IndexOf(crawler.Normal));
        if (!CrawlerPathfinder.Shared.TryFind(start, chest, arriveRadius, halfExtent, chunks, _path)) return false;
        _next = 1;
        _pathIdle = 0f;
        _plannedFor = chest;
        return true;
    }

    private enum PathState { Following, Done, Lost }

    // Moves the next-foothold index past wherever the crawler is found and aims at the one after it
    private PathState Follow(Vector3 position, float halfExtent, Vector3 chest, SurfaceCrawler crawler,
        float deltaTime, out Vector3 aim, out bool letGo)
    {
        aim = chest;
        letGo = false;
        var retarget = CrawlerConfig.PathRetargetBlocks;
        if (Vector3.DistanceSquared(chest, _plannedFor) > retarget * retarget) return PathState.Lost;

        var (x, y, z) = position.BlockCell();
        if (crawler.Attached)
        {
            var face = CrawlFrame.IndexOf(crawler.Normal);
            for (var i = Math.Min(_path.Count - 1, _next + LookAhead); i >= _next; i--)
            {
                if (!_path[i].Is(x, y, z, face)) continue;
                _next = i + 1;
                _pathIdle = 0f;
                break;
            }
        }

        if (_next >= _path.Count) return PathState.Done;
        _pathIdle += deltaTime;
        if (_pathIdle > CrawlerConfig.PathStallSeconds) return PathState.Lost;

        var step = _path[_next];
        var from = _path[_next - 1];
        aim = step.Center(halfExtent);
        if (step.Drop)
            letGo = crawler.Attached && from.X == x && from.Y == y && from.Z == z;
        // A turn onto another face of the same cell: aim into that face, so the crawler walks into it and climbs
        else if (step.SameCell(from))
            aim -= step.Normal;
        return PathState.Following;
    }

    private void Restart(float distance)
    {
        _best = distance;
        _stall = 0f;
    }
}
