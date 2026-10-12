using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.WorldGen;

/// <summary>
/// Classic worm-carved tunnels: a random walk drags a sphere brush through the rock, leaving
/// long connected passages with an opening chamber at the start. The world is split into square
/// XZ cells and each cell deterministically spawns its worms from a hash of its coordinates, so
/// any chunk can rebuild the exact worm set of every nearby cell and carve the part that crosses
/// it. Worm paths are cached per cell (like the column cache) because a vertical stack of chunks
/// and all their neighbors ask for the same cells.
/// </summary>
public static class WormCarver
{
    private const int CellShift = WorldGenerationConfig.CarverCellShift;
    private const int CellBlocks = WorldGenerationConfig.CarverCellBlocks;
    private const int Reach = WorldGenerationConfig.WormReachCells;

    private static readonly Worm[] NoWorms = [];

    // Cell worms are a pure deterministic function of (cellX, cellZ) and the world seed, and a
    // Worm[] -- along with every CaveNode inside it -- is written once in BuildCell and never
    // mutated after publication. That is what lets this share Chunk.ColumnCache's GenerationalCache
    // and its numbered invariants: an eviction, a race or a duplicate entry can only ever cost a
    // recompute, never yield a wrong value.
    //
    // HONEST FINDING (item D2 assumed the two sites were symmetric; they are not). Cells are 64
    // blocks (CarverCellShift = 6), Carve asks for a (2 * WormReachCells + 1)^2 = 25-cell window per
    // chunk, and the whole chunk load sphere (ChunkRadius = 16 chunks = 256 blocks) spans only on the
    // order of 150 cells -- so the 4096-cell capacity is one to two orders of magnitude above the
    // working set and the old wholesale Clear() essentially never fired in play. The LOD builder
    // never touches this cache either; it reads heightmaps only. So this conversion buys UNIFORMITY
    // (AGENTS.md documents the two caches as one deliberate pattern, and a silent divergence would be
    // worse than either policy) plus removal of the all-locks ConcurrentDictionary.Count from the
    // miss path -- and NOT a hit-rate win. The measurable half of D2 is Chunk.ColumnCache.
    //
    // Sizing knobs live in WorldGenerationConfig; the safety argument lives in GenerationalCache.
    private static readonly GenerationalCache<(int X, int Z), Worm[]> CellCache =
        new(WorldGenerationConfig.CellCacheCapacity, WorldGenerationConfig.CellCacheGenerations);

    // Static delegate, not a lambda at the call site: a closure would be a per-miss allocation on
    // every chunk-generation thread.
    private static readonly Func<(int X, int Z), Worm[]> BuildCellFor = static key => BuildCell(key.X, key.Z);

    /// <summary>Drops every cached cell's worms; only <see cref="WorldSeed.Apply"/> calls it.</summary>
    internal static void ResetCellCache() => CellCache.Clear();

    /// <summary>
    /// Carves every worm from the cells within reach through this chunk. Only <paramref name="blocks"/>
    /// is written; neighbouring chunks carve their own share of the same worms.
    /// </summary>
    public static void Carve(byte[] blocks, ColumnData column, int worldX0, int worldY0, int worldZ0)
    {
        var chunkMin = new Vector3(worldX0, worldY0, worldZ0);
        var chunkMax = chunkMin + new Vector3(Chunk.Size);

        var cellX = worldX0 >> CellShift;
        var cellZ = worldZ0 >> CellShift;

        for (var cx = cellX - Reach; cx <= cellX + Reach; cx++)
        for (var cz = cellZ - Reach; cz <= cellZ + Reach; cz++)
        {
            foreach (var worm in GetCellWorms(cx, cz))
            {
                if (!worm.Intersects(chunkMin, chunkMax)) continue;

                foreach (var node in worm.Nodes)
                    CarveNode(blocks, column, worldX0, worldY0, worldZ0, node);
            }
        }
    }

    // Clears the blocks inside one node's (slightly flattened) sphere that lie in this chunk
    private static void CarveNode(byte[] blocks, ColumnData column, int worldX0, int worldY0, int worldZ0, CaveNode node)
    {
        var r = node.Radius;
        var ry = r * WorldGenerationConfig.WormVerticalSquash;
        var c = node.Center;

        // Block b spans [b, b + 1) and is tested at its centre, so both chunks sharing a border
        // evaluate the identical expression for the identical block
        var xMin = Math.Max(0, (int)MathF.Floor(c.X - r - 0.5f) - worldX0);
        var xMax = Math.Min(Chunk.Size - 1, (int)MathF.Ceiling(c.X + r - 0.5f) - worldX0);
        var yMin = Math.Max(0, (int)MathF.Floor(c.Y - ry - 0.5f) - worldY0);
        var yMax = Math.Min(Chunk.Size - 1, (int)MathF.Ceiling(c.Y + ry - 0.5f) - worldY0);
        var zMin = Math.Max(0, (int)MathF.Floor(c.Z - r - 0.5f) - worldZ0);
        var zMax = Math.Min(Chunk.Size - 1, (int)MathF.Ceiling(c.Z + r - 0.5f) - worldZ0);
        if (xMin > xMax || yMin > yMax || zMin > zMax) return;

        var invR2 = 1f / (r * r);
        var invRy = 1f / ry;

        for (var x = xMin; x <= xMax; x++)
        {
            var dx = worldX0 + x + 0.5f - c.X;
            for (var z = zMin; z <= zMax; z++)
            {
                var dz = worldZ0 + z + 0.5f - c.Z;
                var horizontal = (dx * dx + dz * dz) * invR2;
                if (horizontal > 1f) continue;

                var col = (x << Chunk.SizeShift) | z;
                var height = column.Heights[col];
                var margin = WorldGenerationConfig.WormSurfaceMargin * (1f - column.EntranceWeight[col]);

                for (var y = yMin; y <= yMax; y++)
                {
                    var dy = (worldY0 + y + 0.5f - c.Y) * invRy;

                    // Skip the bottom slice of the sphere for a flatter, walkable floor
                    if (dy < -0.7f) continue;
                    if (horizontal + dy * dy > 1f) continue;

                    var depth = height - (worldY0 + y);
                    if (depth < margin) continue; // stays under the surface outside entrance zones

                    blocks[Chunk.Idx(x, y, z)] = BlockType.Air;
                }
            }
        }
    }

    private static Worm[] GetCellWorms(int cellX, int cellZ) => CellCache.GetOrAdd((cellX, cellZ), BuildCellFor);

    private static Worm[] BuildCell(int cellX, int cellZ)
    {
        var rng = new SplitMix64(SplitMix64.Hash(cellX, cellZ, WorldGenerationConfig.Seed));

        var roll = rng.NextFloat();
        var count = roll < WorldGenerationConfig.WormChanceTwo ? 2
            : roll < WorldGenerationConfig.WormChanceTwo + WorldGenerationConfig.WormChanceOne ? 1
            : 0;
        if (count == 0) return NoWorms;

        var worms = new Worm[count];
        for (var i = 0; i < count; i++)
        {
            var startX = (cellX << CellShift) + rng.NextInt(CellBlocks) + 0.5f;
            var startZ = (cellZ << CellShift) + rng.NextInt(CellBlocks) + 0.5f;
            var surface = SurfaceHeight(startX, startZ);

            // Worms starting in an entrance zone begin at the surface and dive, so their opening
            // chamber becomes a visible hole in the ground
            var isEntrance = EntranceWeight(startX, startZ) > 0.5f;
            var startY = isEntrance
                ? surface + 1f
                : surface - rng.NextInt(WorldGenerationConfig.WormStartDepthMin, WorldGenerationConfig.WormStartDepthMax);

            var nodes = new List<CaveNode>(WorldGenerationConfig.WormMaxLength + 8);
            var start = new Vector3(startX, startY, startZ);
            nodes.Add(new CaveNode(start, WorldGenerationConfig.WormRoomRadius));

            var length = rng.NextInt(WorldGenerationConfig.WormMinLength, WorldGenerationConfig.WormMaxLength + 1);
            var yaw = rng.NextFloat() * MathF.Tau;
            var pitch = isEntrance ? -0.4f : 0f;
            Walk(rng, nodes, start, start, yaw, pitch, length, allowBranch: true);

            worms[i] = MakeWorm(nodes);
        }

        return worms;
    }

    // Random walk from `pos`: yaw and pitch drift smoothly, pitch is damped toward level,
    // the radius wanders, and the path is steered down whenever it gets too close to the
    // surface outside entrance zones. Ends early past the excursion cap so a worm can never
    // reach further than WormReachCells cells from the cell it was spawned in.
    private static void Walk(SplitMix64 rng, List<CaveNode> nodes, Vector3 origin, Vector3 pos,
        float yaw, float pitch, int length, bool allowBranch)
    {
        var radius = rng.NextFloat(WorldGenerationConfig.WormMinRadius, WorldGenerationConfig.WormMaxRadius);
        var capSq = WorldGenerationConfig.WormExcursionCap * WorldGenerationConfig.WormExcursionCap;

        for (var step = 0; step < length; step++)
        {
            var cosPitch = MathF.Cos(pitch);
            pos += new Vector3(cosPitch * MathF.Cos(yaw), MathF.Sin(pitch), cosPitch * MathF.Sin(yaw));

            if (Vector3.DistanceSquared(pos, origin) > capSq)
                return;

            yaw += (rng.NextFloat() - 0.5f) * 0.35f;
            pitch = Math.Clamp(pitch + (rng.NextFloat() - 0.5f) * 0.2f, -0.6f, 0.6f) * 0.9f;
            radius = Math.Clamp(radius + (rng.NextFloat() - 0.5f) * 0.3f,
                WorldGenerationConfig.WormMinRadius, WorldGenerationConfig.WormMaxRadius);

            if (EntranceWeight(pos.X, pos.Z) < 0.5f
                && pos.Y + radius > SurfaceHeight(pos.X, pos.Z) - WorldGenerationConfig.WormSurfaceMargin)
                pitch -= 0.1f;

            nodes.Add(new CaveNode(pos, radius));

            // Occasional side passage; branches share the cap and cannot branch again
            if (allowBranch && step > 0 && step % 20 == 0 && rng.NextFloat() < WorldGenerationConfig.WormBranchChance)
            {
                var branchYaw = yaw + (rng.NextFloat() < 0.5f ? -1f : 1f) * MathF.PI * 0.5f;
                Walk(rng, nodes, origin, pos, branchYaw, pitch, (length - step) / 2, allowBranch: false);
            }
        }
    }

    private static Worm MakeWorm(List<CaveNode> nodes)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var node in nodes)
        {
            var extent = new Vector3(node.Radius + 1f);
            min = Vector3.Min(min, node.Center - extent);
            max = Vector3.Max(max, node.Center + extent);
        }

        return new Worm(nodes.ToArray(), min, max);
    }

    private static int SurfaceHeight(float x, float z)
    {
        var bx = MathUtils.FloorToInt(x);
        var bz = MathUtils.FloorToInt(z);
        return ColumnCache.Get(VoxelCoords.WorldToChunk(bx), VoxelCoords.WorldToChunk(bz))
            .Heights[VoxelCoords.ColumnIndex(VoxelCoords.WorldToLocal(bx), VoxelCoords.WorldToLocal(bz))];
    }

    private static float EntranceWeight(float x, float z)
    {
        var bx = MathUtils.FloorToInt(x);
        var bz = MathUtils.FloorToInt(z);
        return ColumnCache.Get(VoxelCoords.WorldToChunk(bx), VoxelCoords.WorldToChunk(bz))
            .EntranceWeight[VoxelCoords.ColumnIndex(VoxelCoords.WorldToLocal(bx), VoxelCoords.WorldToLocal(bz))];
    }
}
