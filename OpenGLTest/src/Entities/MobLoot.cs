using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// The loot toss, GL-free: the launch a killed mob's every dropped item gets on top of a drop's own
/// small pop (<see cref="ItemDrop"/>), a random horizontal direction at
/// <see cref="WorldGenerationConfig.MobLootTossMinSpeed"/>..<see cref="WorldGenerationConfig.MobLootTossMaxSpeed"/>
/// plus <see cref="WorldGenerationConfig.MobLootTossLift"/> upward, so the loot arcs out of the body
/// instead of appearing at its feet. The mob managers pass it to <see cref="DropManager.SpawnAt"/>.
/// </summary>
public static class MobLoot
{
    /// <summary>The toss for an <paramref name="angleRoll"/> and a <paramref name="speedRoll"/>, each in
    /// [0, 1): the first picks the horizontal heading, the second the outward speed between the bounds.</summary>
    public static Vector3 Toss(float angleRoll, float speedRoll)
    {
        var angle = angleRoll * MathF.Tau;
        var speed = MathUtils.Lerp(WorldGenerationConfig.MobLootTossMinSpeed, WorldGenerationConfig.MobLootTossMaxSpeed,
            MathUtils.Clamp01(speedRoll));
        return new Vector3(MathF.Cos(angle) * speed, WorldGenerationConfig.MobLootTossLift, MathF.Sin(angle) * speed);
    }

    /// <summary>A toss from two fresh uniform rolls.</summary>
    public static Vector3 RandomToss() => Toss(Random.Shared.NextSingle(), Random.Shared.NextSingle());
}
