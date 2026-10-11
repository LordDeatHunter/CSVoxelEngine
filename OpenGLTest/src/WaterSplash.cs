using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// The water-splash rule, GL-free beside <see cref="LandingDust"/>: how many droplets a body
/// crossing the water surface throws and how fast, from the speed it crossed at. Nothing under
/// <see cref="WorldGenerationConfig.WaterSplashMinSpeed"/> (a body drifting across), then both grow
/// along <see cref="MathUtils.ImpactStrength"/> to their caps at
/// <see cref="WorldGenerationConfig.WaterSplashFullSpeed"/>; the count also scales with the body's size.
/// <see cref="Track"/> steps one body's <see cref="WaterSplashTracker"/> and throws the droplets: pale
/// <see cref="DebrisLook.Colour"/> chips from the water top of the column it crossed
/// (<see cref="ChunkManager.GetFluidColumnTopGlobal"/>), never off lava. Cosmetic: the world runs it in
/// singleplayer and on a client, never on a headless server.
/// </summary>
public static class WaterSplash
{
    /// <summary>Droplets a body of <paramref name="scale"/> (1 for the player) crossing the surface at
    /// <paramref name="speed"/> blocks per second throws; 0 under the minimum speed.</summary>
    public static int Count(float speed, float scale = 1f)
    {
        if (!(speed >= WorldGenerationConfig.WaterSplashMinSpeed) || !(scale > 0f)) return 0;
        return (int)MathF.Round(MathUtils.Lerp(WorldGenerationConfig.WaterSplashMinCount,
            WorldGenerationConfig.WaterSplashMaxCount, Strength(speed)) * scale);
    }

    /// <summary>Launch speed in blocks per second of those droplets; 0 under the minimum speed.</summary>
    public static float Speed(float speed)
    {
        if (!(speed >= WorldGenerationConfig.WaterSplashMinSpeed)) return 0f;
        return MathUtils.Lerp(WorldGenerationConfig.WaterSplashMinLaunch, WorldGenerationConfig.WaterSplashMaxLaunch,
            Strength(speed));
    }

    /// <summary>
    /// One frame of a body whose lowest point is <paramref name="bottom"/>: steps its
    /// <paramref name="tracker"/> (in water when that point is under the fluid surface of its cell, or
    /// as <paramref name="inWater"/> says when given, as the player's swim state does) and, on an entry or
    /// exit, throws <see cref="Count"/> droplets up off the water top of the column it crossed, out of a
    /// ring <paramref name="scale"/> times a player's width. Returns how many it threw (0 off lava, where
    /// no water is found, or for a crossing too slow to splash).
    /// </summary>
    public static int Track(WaterSplashTracker tracker, Vector3 bottom, float deltaTime, float scale,
        ChunkManager chunks, BlockDebrisSystem debris, bool? inWater = null, Random? random = null)
    {
        var x = MathUtils.FloorToInt(bottom.X);
        var y = MathUtils.FloorToInt(bottom.Y);
        var z = MathUtils.FloorToInt(bottom.Z);
        var wet = inWater ?? bottom.Y < chunks.GetFluidSurfaceGlobal(x, y, z);
        if (!tracker.Step(wet, bottom.Y, deltaTime, out var speed)) return 0;
        var count = Count(speed, scale);
        if (count <= 0 || !TryWaterTop(chunks, x, y, z, out var top)) return 0;

        debris.Splash(new Vector3(bottom.X, top, bottom.Z), WorldGenerationConfig.WaterSplashColor,
            PlayerPhysics.PlayerRadius * scale, count, Speed(speed), WorldGenerationConfig.WaterSplashSize, random);
        return count;
    }

    // The top of the water the body crossed: its own cell going in, the cell under it coming out.
    // A glowing fluid (lava) throws no water droplets.
    private static bool TryWaterTop(ChunkManager chunks, int x, int y, int z, out float top)
    {
        for (var cy = y; cy >= y - 1; cy--)
        {
            if (!chunks.TryGetCell(x, cy, z, out var id, out _) || !BlockType.IsFluid(id)) continue;
            top = chunks.GetFluidColumnTopGlobal(x, cy, z);
            return BlockType.Emission[id] == 0 && float.IsFinite(top);
        }

        top = 0f;
        return false;
    }

    private static float Strength(float speed) =>
        MathUtils.ImpactStrength(speed, WorldGenerationConfig.WaterSplashMinSpeed,
            WorldGenerationConfig.WaterSplashFullSpeed);
}
