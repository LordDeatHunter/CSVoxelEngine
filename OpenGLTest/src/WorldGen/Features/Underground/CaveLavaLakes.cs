using System;
using System.Collections.Generic;
using VoxelEngine.Chunks;

namespace VoxelEngine.WorldGen.Features.Underground;

/// <summary>Source-filled connected depressions in actual cave terrain. A bounded flood must
/// find natural solid bottom and walls: any spill or unknown extent rejects the entire level.
/// Immutable seed/position plans span chunks; each chunk writes only its own cells.</summary>
internal static class CaveLavaLakes
{
    private const int Shift = WorldGenerationConfig.CaveLavaLakeCellShift;
    private const int Cell = 1 << Shift;
    internal readonly record struct CellPosition(int X, int Y, int Z);
    private static readonly (int X, int Y, int Z)[] Neighbors =
        [(-1, 0, 0), (1, 0, 0), (0, -1, 0), (0, 1, 0), (0, 0, -1), (0, 0, 1)];
    private static readonly GenerationalCache<(int X, int Y, int Z, int Seed), Lazy<CellPosition[]>> Plans = new(
        WorldGenerationConfig.CaveLavaLakeCacheCapacity, WorldGenerationConfig.CaveLavaLakeCacheGenerations);
    private static readonly Func<(int X, int Y, int Z, int Seed), Lazy<CellPosition[]>> BuildFor = static key => new(() => Build(key));

    internal static float Chance(int y) => MathUtils.Lerp(WorldGenerationConfig.CaveLavaLakeChanceShallow,
        WorldGenerationConfig.CaveLavaLakeChanceDeep, Depth(y));

    private static float Depth(int y) => MathUtils.SmoothStep(WorldGenerationConfig.LavaMaxY,
        WorldGenerationConfig.BarrierTopY, y);

    /// <summary>A deeper site can admit a larger natural depression, with overlapping size distributions.</summary>
    internal static int AreaBudget(int y, float roll) => (int)MathUtils.Lerp(
        WorldGenerationConfig.CaveLavaLakeMinArea,
        MathUtils.Lerp(WorldGenerationConfig.CaveLavaLakeShallowArea, WorldGenerationConfig.CaveLavaLakeDeepArea, Depth(y)),
        roll);

    // Readers of a shared cached lazy share its realization. Racing misses/eviction can
    // still compute separate plans; every result remains a pure function of the key.
    internal static CellPosition[] Plan(int cx, int cy, int cz) =>
        Plans.GetOrAdd((cx, cy, cz, WorldGenerationConfig.Seed), BuildFor).Value;

    internal static bool Contains(int x, int y, int z) =>
        Array.IndexOf(Plan(x >> Shift, y >> Shift, z >> Shift), new CellPosition(x, y, z)) >= 0;

    private static CellPosition[] Build((int X, int Y, int Z, int Seed) key)
    {
        var x0 = key.X << Shift;
        var y0 = key.Y << Shift;
        var z0 = key.Z << Shift;
        if (y0 > WorldGenerationConfig.LavaMaxY || y0 + Cell <= WorldGenerationConfig.BarrierTopY + 1) return [];
        var h = SplitMix64.Hash(key.X ^ (key.Y * 0x9E37), key.Z, key.Seed ^ 0x5E83);
        if (SplitMix64.Unit(h, 0) >= Chance(y0 + Cell / 2)) return [];
        var terrain = new CaveTerrainProbe();
        CellPosition[] best = [];
        var trials = 0;
        // Search seed-varying vertical transects for real cave floors, rather than stamping
        // a shape or adding support. The ownership cell is a rejection bound, never a dam.
        for (var n = 0; n < WorldGenerationConfig.CaveLavaLakeSearchColumns; n++)
        {
            var hash = SplitMix64.Mix(h + (ulong)n + 1);
            var x = x0 + Cell / 4 + (int)(hash % (Cell / 2));
            var z = z0 + Cell / 4 + (int)(SplitMix64.Mix(hash) % (Cell / 2));
            for (var y = Math.Max(y0 + 1, WorldGenerationConfig.BarrierTopY + 2);
                 y < Math.Min(y0 + Cell - 1, WorldGenerationConfig.LavaMaxY); y++)
            {
                if (terrain.At(x, y, z) != BlockType.Air || !IsRock(terrain.At(x, y - 1, z))) continue;
                var rise = (int)MathUtils.Lerp(WorldGenerationConfig.CaveLavaLakeShallowRise,
                    WorldGenerationConfig.CaveLavaLakeDeepRise, Depth(y));
                for (var level = Math.Min(y + rise, WorldGenerationConfig.LavaMaxY); level >= y; level--)
                {
                    var cells = Flood(new CellPosition(x, y, z), level, key.X, key.Y, key.Z,
                        AreaBudget(y, SplitMix64.Unit(hash, 2)), terrain.At);
                    if (cells.Length > best.Length) best = cells;
                }
                if (++trials >= WorldGenerationConfig.CaveLavaLakeSearchFloors) return best;
            }
        }
        return best;
    }

    /// <summary>Floods all connected air below a flat level, including under overhangs.
    /// Six-face closure is verified against terrain, not an assumed circle or chunk edge.</summary>
    internal static CellPosition[] Flood(CellPosition start, int level, int cx, int cy, int cz,
        int areaBudget, Func<int, int, int, ushort> terrain)
    {
        if (terrain(start.X, start.Y, start.Z) != BlockType.Air || start.Y > level) return [];
        var pending = new Queue<CellPosition>();
        var reached = new HashSet<CellPosition>();
        var surface = 0;
        var exposed = 0;
        pending.Enqueue(start);
        reached.Add(start);
        while (pending.TryDequeue(out var p))
        {
            if (p.X >> Shift != cx || p.Y >> Shift != cy || p.Z >> Shift != cz
                || p.Y <= WorldGenerationConfig.BarrierTopY + 1 || level > WorldGenerationConfig.LavaMaxY
                || level - p.Y > WorldGenerationConfig.CaveLavaLakeMaxDepth
                || reached.Count > areaBudget * WorldGenerationConfig.CaveLavaLakeMaxDepth) return [];
            var column = ColumnCache.Get(p.X >> Chunk.SizeShift, p.Z >> Chunk.SizeShift);
            var i = VoxelCoords.ColumnIndex(p.X & Chunk.SizeMask, p.Z & Chunk.SizeMask);
            if (level + WorldGenerationConfig.CaveLavaLakeAirHeight > column.Heights[i] - WorldGenerationConfig.CaveLavaLakeBurial)
                return [];
            if (p.Y == level)
            {
                if (++surface > areaBudget) return [];
                if (terrain(p.X, level + 1, p.Z) == BlockType.Air) exposed++;
            }
            foreach (var (dx, dy, dz) in Neighbors)
            {
                var q = new CellPosition(p.X + dx, p.Y + dy, p.Z + dz);
                var id = terrain(q.X, q.Y, q.Z);
                if (BlockType.IsFluid(id)) return [];
                if (q.Y > level) continue;
                if (IsRock(id)) continue;
                if (id != BlockType.Air) return [];
                if (reached.Add(q)) pending.Enqueue(q);
            }
        }
        // A covered void is not a visible pool. Small puddles retain the separate source path.
        return exposed >= WorldGenerationConfig.CaveLavaLakeMinArea ? [.. reached] : [];
    }

    private static bool IsRock(ushort id) => BlockType.IsSolid(id) && BlockType.Occludes(id) && !BlockType.IsFluid(id);

    internal static void Stamp(ushort[] blocks, byte[]? state, int x0, int y0, int z0)
    {
        foreach (var p in Plan(x0 >> Shift, y0 >> Shift, z0 >> Shift))
        {
            var x = p.X - x0;
            var y = p.Y - y0;
            var z = p.Z - z0;
            if ((uint)x >= Chunk.Size || (uint)y >= Chunk.Size || (uint)z >= Chunk.Size) continue;
            var i = Chunk.Idx(x, y, z);
            // The probe omits only procedural cave life. Clear submerged points/plants,
            // using their support behavior, and reset the new id's source state explicitly.
            var id = blocks[i];
            if (id != BlockType.Air && (BlockType.IsSolid(id) || BlockType.ById[id]?.NeedsSupport != true)) continue;
            blocks[i] = LavaGenerator.LavaBlock;
            if (state is not null) state[i] = 0;
        }
    }
}
