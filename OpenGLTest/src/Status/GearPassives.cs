using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Config;
using VoxelEngine.Entities;
using VoxelEngine.Items;
using VoxelEngine.Player;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Status;

/// <summary>
/// The passive triggers of worn gear (an item's <c>equip.passives</c>), applied through <see cref="StatusEffects"/>
/// in the authoritative simulation only: <see cref="PassiveTrigger.OnHit"/> puts its effect on the mob a blow landed
/// on, <see cref="PassiveTrigger.OnDamaged"/> on the mobs around the wearer when a mob hurt them, and
/// <see cref="PassiveTrigger.LowHealth"/> on the mobs around the wearer while their health is under its share (at most
/// once per <see cref="GearConfig.PassiveLowHealthCooldownSeconds"/>). There is no ability key: a passive just
/// happens. The chance roll is passed in (0..1), so tests are deterministic.
/// </summary>
public static class GearPassives
{
    /// <summary>Fires the on-hit passives of <paramref name="striker"/>'s worn gear on <paramref name="mob"/>; returns
    /// how many effects applied.</summary>
    public static int OnHit(PlayerHealth? striker, Mob mob, Func<float> roll)
    {
        if (striker?.Gear is not { } gear) return 0;
        var applied = 0;
        foreach (var passive in PassivesOf(gear, PassiveTrigger.OnHit))
            if (roll() < passive.Chance && StatusEffects.Apply(mob, passive.Effect, passive.Scale))
                applied++;
        return applied;
    }

    /// <summary>
    /// Fires the wearer's on-damaged passives when a mob blow landed since the last call, and their low-health ones
    /// while health is under their share and the cooldown is up, on every mob within
    /// <see cref="GearConfig.PassiveAuraRadiusBlocks"/> of <paramref name="center"/>. Returns how many effects applied.
    /// </summary>
    public static int Retaliate(PlayerHealth wearer, Vector3 center, IReadOnlyList<Mob> mobs, float deltaTime,
        Func<float> roll)
    {
        wearer.TickPassiveCooldown(deltaTime);
        if (wearer.Gear is not { } gear || wearer.IsDead) return 0;

        var applied = 0;
        if (wearer.ConsumeMobBlow())
            foreach (var passive in PassivesOf(gear, PassiveTrigger.OnDamaged))
                applied += ApplyAround(passive, center, mobs, roll);

        var share = (float)wearer.Health / wearer.Max;
        foreach (var passive in PassivesOf(gear, PassiveTrigger.LowHealth))
        {
            if (share >= passive.Below || !wearer.PassiveReady) continue;
            var landed = ApplyAround(passive, center, mobs, roll);
            if (landed == 0) continue;
            wearer.StartPassiveCooldown(GearConfig.PassiveLowHealthCooldownSeconds);
            applied += landed;
        }
        return applied;
    }

    private static int ApplyAround(GearPassive passive, Vector3 center, IReadOnlyList<Mob> mobs, Func<float> roll)
    {
        var radiusSquared = GearConfig.PassiveAuraRadiusBlocks * GearConfig.PassiveAuraRadiusBlocks;
        var applied = 0;
        for (var i = 0; i < mobs.Count; i++)
        {
            if (Vector3.DistanceSquared(mobs[i].Position, center) > radiusSquared) continue;
            if (roll() < passive.Chance && StatusEffects.Apply(mobs[i], passive.Effect, passive.Scale))
                applied++;
        }
        return applied;
    }

    private static IEnumerable<GearPassive> PassivesOf(Equipment gear, PassiveTrigger trigger)
    {
        foreach (var stack in gear.Worn())
        {
            if (stack.Item is not GearItem item) continue;
            foreach (var passive in item.Info.Passives)
                if (passive.Trigger == trigger)
                    yield return passive;
        }
    }
}
