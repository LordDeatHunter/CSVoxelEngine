using System;

namespace VoxelEngine.WorldGen.Caves;

/// <summary>Asymmetric clusters of overlapping lobes form coherent desert rooms inside their own
/// site cells. Long, narrower tunnels are sampled separately by NoiseCaves and can pierce the walls.
/// Cached geometry is immutable and depends only on world-space cell coordinates and seed.</summary>
internal static class DesertCaves
{
    private const int Cell = WorldGenerationConfig.DesertPocketCellBlocks;
    private const int Height = WorldGenerationConfig.DesertPocketCellHeight;
    private readonly record struct Lobe(float X, float Z, float Rx, float Rz, float Cos, float Sin)
    {
        public bool Contains(float x, float z)
        {
            var dx = x - X;
            var dz = z - Z;
            var u = (dx * Cos + dz * Sin) / Rx;
            var w = (dz * Cos - dx * Sin) / Rz;
            return u * u + w * w < 1f;
        }
    }
    private readonly record struct Site(int X, int Y, int Z, int Vertical, float FloorScale, float Roll, Lobe[] Lobes);
    private static readonly GenerationalCache<(int X, int Y, int Z, int Seed), Site> Sites = new(
        WorldGenerationConfig.DesertPocketCacheCapacity, WorldGenerationConfig.DesertPocketCacheGenerations);
    private static readonly Func<(int X, int Y, int Z, int Seed), Site> BuildFor = Build;

    private static Site Build((int X, int Y, int Z, int Seed) key)
    {
        var h = SplitMix64.Hash(key.X ^ (key.Y * 0x9E37), key.Z, key.Seed ^ 0x5E81);
        var radius = WorldGenerationConfig.DesertPocketMinRadius + (int)(SplitMix64.Mix(h + 1)
            % (ulong)(WorldGenerationConfig.DesertPocketMaxRadius - WorldGenerationConfig.DesertPocketMinRadius + 1));
        var vertical = WorldGenerationConfig.DesertPocketMinHeight + (int)(SplitMix64.Mix(h + 2)
            % (ulong)(WorldGenerationConfig.DesertPocketMaxHeight - WorldGenerationConfig.DesertPocketMinHeight + 1));
        var wall = WorldGenerationConfig.DesertPocketWallBlocks;
        var mx = radius + wall;
        var my = vertical + wall;
        var px = key.X * Cell + mx + (int)(SplitMix64.Mix(h + 3) % (ulong)(Cell - 2 * mx));
        var py = key.Y * Height + my + (int)(SplitMix64.Mix(h + 4) % (ulong)(Height - 2 * my));
        var pz = key.Z * Cell + mx + (int)(SplitMix64.Mix(h + 5) % (ulong)(Cell - 2 * mx));
        var count = WorldGenerationConfig.DesertPocketMinLobes + (int)(SplitMix64.Mix(h + 6)
            % (ulong)(WorldGenerationConfig.DesertPocketMaxLobes - WorldGenerationConfig.DesertPocketMinLobes + 1));
        var lobes = new Lobe[count + 1];
        var phase = SplitMix64.Unit(h, 7) * MathF.Tau;
        var core = WorldGenerationConfig.DesertPocketCoreRadius;
        lobes[0] = new Lobe(0, 0, radius * core, radius * core, 1, 0);
        for (var i = 0; i < count; i++)
        {
            var hash = SplitMix64.Mix(h + (ulong)(8 + i));
            var angle = phase + MathF.Tau * (i + (SplitMix64.Unit(hash, 0) - 0.5f)
                * WorldGenerationConfig.DesertPocketAngleJitter) / count;
            var sin = MathF.Sin(angle);
            var cos = MathF.Cos(angle);
            var offset = MathUtils.Lerp(WorldGenerationConfig.DesertPocketMinOffset,
                WorldGenerationConfig.DesertPocketMaxOffset, SplitMix64.Unit(hash, 1));
            // Each lobe's center lies inside the core. Its farthest horizontal extent is
            // bounded by radius, so the original ownership margin still protects every wall.
            var remaining = radius * (1 - offset);
            var width = MathUtils.Lerp(WorldGenerationConfig.DesertPocketMinWidth, 1f, SplitMix64.Unit(hash, 2));
            lobes[i + 1] = new Lobe(radius * offset * cos, radius * offset * sin,
                remaining, remaining * width, cos, sin);
        }
        return new Site(px, py, pz, vertical,
            MathUtils.Lerp(WorldGenerationConfig.DesertPocketMinFloorScale, 1f, SplitMix64.Unit(h, 13)),
            SplitMix64.Unit(h, 0), lobes);
    }

    internal static bool InPocket(int x, int y, int z, float depth, float gate)
    {
        if (gate <= 0) return false;
        var cx = MathUtils.FloorToInt(x / (float)Cell);
        var cy = MathUtils.FloorToInt(y / (float)Height);
        var cz = MathUtils.FloorToInt(z / (float)Cell);
        var site = Sites.GetOrAdd((cx, cy, cz, WorldGenerationConfig.Seed), BuildFor);
        var chance = MathUtils.Lerp(WorldGenerationConfig.DesertPocketChanceShallow,
            WorldGenerationConfig.DesertPocketChanceDeep, MathUtils.SmoothStep(WorldGenerationConfig.DesertPocketDepthStart,
                WorldGenerationConfig.DesertPocketDepthEnd, depth));
        if (site.Roll >= chance) return false;
        var dy = (y - site.Y) / (site.Vertical * (y < site.Y ? site.FloorScale : 1f));
        var crossSection = gate * gate - dy * dy;
        if (crossSection <= 0) return false;
        // Taper lobe centers together with their radii: every horizontal slice remains
        // connected through the core, including near the roof and floor.
        var taper = MathF.Sqrt(crossSection);
        var dx = (x - site.X) / taper;
        var dz = (z - site.Z) / taper;
        foreach (var lobe in site.Lobes)
            if (lobe.Contains(dx, dz)) return true;
        return false;
    }
}
