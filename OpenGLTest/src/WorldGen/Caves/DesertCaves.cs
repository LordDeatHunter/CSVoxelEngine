using System;

namespace VoxelEngine.WorldGen.Caves;

/// <summary>Large desert rooms stay inside their own site cell, leaving walls between pockets.
/// Long, narrower tunnels are sampled separately by NoiseCaves and can pierce those walls.</summary>
internal static class DesertCaves
{
    private const int Cell = WorldGenerationConfig.DesertPocketCellBlocks;
    private const int Height = WorldGenerationConfig.DesertPocketCellHeight;

    internal static bool InPocket(int x, int y, int z, float depth, float gate)
    {
        var cx = MathUtils.FloorToInt(x / (float)Cell);
        var cy = MathUtils.FloorToInt(y / (float)Height);
        var cz = MathUtils.FloorToInt(z / (float)Cell);
        var h = SplitMix64.Hash(cx ^ (cy * 0x9E37), cz, WorldGenerationConfig.Seed ^ 0x5E81);
        var chance = MathUtils.Lerp(WorldGenerationConfig.DesertPocketChanceShallow,
            WorldGenerationConfig.DesertPocketChanceDeep, MathUtils.SmoothStep(WorldGenerationConfig.DesertPocketDepthStart, WorldGenerationConfig.DesertPocketDepthEnd, depth));
        if (SplitMix64.Unit(h, 0) >= chance) return false;
        var radius = WorldGenerationConfig.DesertPocketMinRadius + (int)(SplitMix64.Mix(h + 1)
            % (ulong)(WorldGenerationConfig.DesertPocketMaxRadius - WorldGenerationConfig.DesertPocketMinRadius + 1));
        var vertical = WorldGenerationConfig.DesertPocketMinHeight + (int)(SplitMix64.Mix(h + 2)
            % (ulong)(WorldGenerationConfig.DesertPocketMaxHeight - WorldGenerationConfig.DesertPocketMinHeight + 1));
        var wall = WorldGenerationConfig.DesertPocketWallBlocks;
        var mx = radius + wall;
        var my = vertical + wall;
        var px = cx * Cell + mx + (int)(SplitMix64.Mix(h + 3) % (ulong)(Cell - 2 * mx));
        var py = cy * Height + my + (int)(SplitMix64.Mix(h + 4) % (ulong)(Height - 2 * my));
        var pz = cz * Cell + mx + (int)(SplitMix64.Mix(h + 5) % (ulong)(Cell - 2 * mx));
        var dx = (x - px) / (float)radius;
        var dy = (y - py) / (float)vertical;
        var dz = (z - pz) / (float)radius;
        return dx * dx + dy * dy + dz * dz < gate * gate;
    }
}
