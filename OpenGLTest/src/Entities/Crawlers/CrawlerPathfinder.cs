using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Entities.Crawlers;

/// <summary>
/// A* over the faces of the block grid, the graph a crawler walks: a node is a <see cref="CrawlerFoothold"/> (an air
/// cell the body fits in against one solid neighbor's face) and its moves are the ones <see cref="SurfaceCrawler"/>
/// makes on its own when its heading carries it there: along the face to the next cell, up onto a wall it walks into
/// (a concave turn in the same cell), round the edge it walks off (a convex turn onto the side of the block it stood
/// on), and, from a wall or a ceiling, letting go to fall straight down onto the floor below. The search runs toward
/// a goal point and stops at the first foothold within the arrival radius; with none inside its node budget it
/// settles for the foothold nearest the goal, if that is a real gain on the start. GL-free; one instance per thread
/// (<see cref="Shared"/>), reusing its buffers, so a plan allocates nothing once warm.
/// </summary>
public sealed class CrawlerPathfinder
{
    // A turn round an edge moves the body about a block along and a block down
    private const float WrapCost = 1.4f;
    // A fall costs little: a crawler never takes fall damage
    private const float DropCostPerBlock = 0.3f;
    // Leans the search toward the goal (above 1 trades the shortest path for fewer expanded nodes)
    private const float HeuristicWeight = 1.2f;
    // A path that ends short of the goal must end at least this much closer to it than the start
    private const float MinGainBlocks = 1f;

    [ThreadStatic] private static CrawlerPathfinder? _shared;

    private readonly Dictionary<long, int> _slots = new();
    private readonly List<Node> _nodes = [];
    private readonly PriorityQueue<int, float> _open = new();
    private readonly List<(CrawlerFoothold Foothold, float Cost)> _moves = [];

    private struct Node
    {
        public CrawlerFoothold Foothold;
        public int Parent;
        public float Cost;
        public bool Closed;
    }

    /// <summary>This thread's pathfinder (the game's and the server's main thread, each test's own).</summary>
    public static CrawlerPathfinder Shared => _shared ??= new CrawlerPathfinder();

    /// <summary>
    /// Fills <paramref name="path"/> with the footholds from <paramref name="start"/> (first) toward
    /// <paramref name="goal"/> for a body of <paramref name="halfExtent"/>: to the first foothold whose body center is
    /// within <paramref name="arriveRadius"/> of the goal, else to the one nearest it among the
    /// <see cref="CrawlerConfig.PathMaxNodes"/> expanded. False (and an empty path) when no foothold is a gain of at
    /// least a block on the start.
    /// </summary>
    public bool TryFind(CrawlerFoothold start, Vector3 goal, float arriveRadius, float halfExtent, ChunkManager chunks,
        List<CrawlerFoothold> path)
    {
        path.Clear();
        _slots.Clear();
        _nodes.Clear();
        _open.Clear();

        start = start with { Drop = false };
        var startDistance = Vector3.Distance(start.Center(halfExtent), goal);
        _slots[Key(start, start)] = 0;
        _nodes.Add(new Node { Foothold = start, Parent = -1 });
        _open.Enqueue(0, startDistance * HeuristicWeight);

        var best = 0;
        var bestDistance = startDistance;
        var found = false;
        var expanded = 0;
        while (expanded < CrawlerConfig.PathMaxNodes && _open.TryDequeue(out var slot, out _))
        {
            var node = _nodes[slot];
            if (node.Closed) continue;
            node.Closed = true;
            _nodes[slot] = node;
            expanded++;

            var distance = Vector3.Distance(node.Foothold.Center(halfExtent), goal);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = slot;
            }
            if (distance <= arriveRadius)
            {
                found = true;
                break;
            }

            _moves.Clear();
            Moves(node.Foothold, halfExtent, chunks, _moves);
            for (var i = 0; i < _moves.Count; i++)
                Relax(slot, node.Cost, _moves[i], start, halfExtent, goal);
        }

        if (!found && bestDistance > startDistance - MinGainBlocks) return false;
        for (var s = best; s >= 0; s = _nodes[s].Parent)
            path.Add(_nodes[s].Foothold);
        path.Reverse();
        if (path.Count >= 2) return true;
        path.Clear();
        return false;
    }

    // Opens or improves the node a move from the expanded node reaches
    private void Relax(int from, float fromCost, (CrawlerFoothold Foothold, float Cost) move, CrawlerFoothold start,
        float halfExtent, Vector3 goal)
    {
        var cost = fromCost + move.Cost;
        var priority = cost + Vector3.Distance(move.Foothold.Center(halfExtent), goal) * HeuristicWeight;
        var key = Key(move.Foothold, start);
        if (_slots.TryGetValue(key, out var existing))
        {
            var other = _nodes[existing];
            if (other.Closed || other.Cost <= cost) return;
            other.Foothold = move.Foothold;
            other.Parent = from;
            other.Cost = cost;
            _nodes[existing] = other;
            _open.Enqueue(existing, priority);
            return;
        }

        _slots[key] = _nodes.Count;
        _nodes.Add(new Node { Foothold = move.Foothold, Parent = from, Cost = cost });
        _open.Enqueue(_nodes.Count - 1, priority);
    }

    /// <summary>The footholds a crawler on <paramref name="from"/> reaches in one move, with their costs.</summary>
    internal static void Moves(CrawlerFoothold from, float halfExtent, ChunkManager chunks,
        List<(CrawlerFoothold Foothold, float Cost)> into)
    {
        var normal = from.Normal;
        for (var face = 1; face <= 6; face++)
        {
            var t = CrawlFrame.NormalOf(face);
            if (MathF.Abs(Vector3.Dot(t, normal)) > 0.5f) continue;
            var (tx, ty, tz) = Cell(t);
            int ax = from.X + tx, ay = from.Y + ty, az = from.Z + tz;

            // A wall in the way: climb onto it without leaving the cell
            if (Solid(chunks, ax, ay, az))
            {
                var climbed = CrawlFrame.IndexOf(-t);
                if (Fits(chunks, from.X, from.Y, from.Z, -t, halfExtent))
                    into.Add((new CrawlerFoothold(from.X, from.Y, from.Z, climbed), 1f));
                continue;
            }

            if (!Fits(chunks, ax, ay, az, normal, halfExtent)) continue;
            var (nx, ny, nz) = Cell(normal);
            int bx = ax - nx, by = ay - ny, bz = az - nz;
            // The face goes on: one cell along it
            if (Solid(chunks, bx, by, bz))
            {
                into.Add((new CrawlerFoothold(ax, ay, az, from.Face), 1f));
                continue;
            }

            // The face ends: round the edge onto the side of the block it held
            if (Fits(chunks, bx, by, bz, t, halfExtent))
                into.Add((new CrawlerFoothold(bx, by, bz, face), WrapCost));
        }

        // A floor is left by its edges; a wall or a ceiling may also be let go of, onto the floor straight below
        if (normal.Y > 0.5f) return;
        for (var k = 1; k <= CrawlerConfig.PathMaxDropBlocks; k++)
        {
            var id = chunks.GetBlockGlobal(from.X, from.Y - k, from.Z);
            if (BlockType.IsFluid(id)) return;
            if (!BlockType.IsSolid(id)) continue;
            // A floor right under the cell is the climb onto it, not a fall
            if (k > 1)
                into.Add((new CrawlerFoothold(from.X, from.Y - k + 1, from.Z, CrawlFrame.IndexOf(Vector3.UnitY), true),
                    1f + k * DropCostPerBlock));
            return;
        }
    }

    // Whether a body clinging to the face with normal n has room in cell (x, y, z): the cell is open, and a body
    // wider than a cell also needs the cell in front of it
    private static bool Fits(ChunkManager chunks, int x, int y, int z, Vector3 n, float halfExtent)
    {
        if (!Open(chunks, x, y, z)) return false;
        if (halfExtent <= 0.5f) return true;
        var (nx, ny, nz) = Cell(n);
        return Open(chunks, x + nx, y + ny, z + nz);
    }

    private static bool Solid(ChunkManager chunks, int x, int y, int z) =>
        BlockType.IsSolid(chunks.GetBlockGlobal(x, y, z));

    private static bool Open(ChunkManager chunks, int x, int y, int z)
    {
        var id = chunks.GetBlockGlobal(x, y, z);
        return !BlockType.IsSolid(id) && !BlockType.IsFluid(id);
    }

    private static (int X, int Y, int Z) Cell(Vector3 axis) =>
        ((int)MathF.Round(axis.X), (int)MathF.Round(axis.Y), (int)MathF.Round(axis.Z));

    // A foothold's identity (its cell and face, not how it was reached), relative to the start so it packs in a long
    private static long Key(CrawlerFoothold foothold, CrawlerFoothold start) =>
        ((long)((foothold.X - start.X) & 0xFFFF) << 35)
        | ((long)((foothold.Y - start.Y) & 0xFFFF) << 19)
        | ((long)((foothold.Z - start.Z) & 0xFFFF) << 3)
        | (long)(foothold.Face & 0x7);
}
