using System;
using System.Numerics;
using VoxelEngine.Items;

namespace VoxelEngine.Interaction;

/// <summary>
/// GL-free player-combat rules shared by the punch loop and the tests: the reach and spread of
/// the forward punch cone and the damage one punch deals. A punch only connects with a target
/// inside a narrow cone ahead of the eye, so facing matters; block mining numbers live in
/// <see cref="Mining"/>.
/// </summary>
public static class Combat
{
    /// <summary>How far ahead the punch connects, in blocks (eye to body center).</summary>
    public const float PunchReach = 7f;

    /// <summary>Half the punch cone's opening angle, in degrees.</summary>
    public const float PunchHalfAngleDegrees = 30f;

    /// <summary>Cosine of <see cref="PunchHalfAngleDegrees"/>: a target is in the cone when the
    /// view direction's dot with the unit direction to it is at least this.</summary>
    public static readonly float PunchMinDot = MathUtils.CosDeg(PunchHalfAngleDegrees);

    /// <summary>
    /// Whether a body at <paramref name="target"/> sits inside the forward punch cone from
    /// <paramref name="origin"/> facing <paramref name="direction"/>: within
    /// <see cref="PunchReach"/> and within <see cref="PunchHalfAngleDegrees"/> of the aim.
    /// Reports the eye-to-body distance so the caller can pick the nearest target.
    /// </summary>
    public static bool InPunchCone(Vector3 origin, Vector3 direction, Vector3 target, out float distance)
    {
        var to = target - origin;
        distance = to.Length();
        if (distance < 1e-4f || distance > PunchReach) return false;
        return Vector3.Dot(direction, to / distance) >= PunchMinDot;
    }

    /// <summary>Damage one punch deals, rounded to whole hit points: the held item's
    /// <c>entityDamage</c>, or the bare-hand baseline when nothing is held.</summary>
    public static int PunchDamage(Item? held, float damageFactor = 1f) =>
        (int)MathF.Round(Mining.EntityDamage(held) * damageFactor, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Whether a body at <paramref name="target"/> sits inside the forward hemisphere from
    /// <paramref name="origin"/> facing <paramref name="direction"/>: within the punch reach
    /// (<see cref="PunchReach"/>) and anywhere in front of the aim. The blade's wide sweep uses
    /// this instead of the narrow punch cone. Reports the distance for nearest-target selection.
    /// </summary>
    public static bool InFrontHemisphere(Vector3 origin, Vector3 direction, Vector3 target, out float distance)
    {
        var to = target - origin;
        distance = to.Length();
        if (distance < 1e-4f || distance > PunchReach) return false;
        return Vector3.Dot(direction, to) > 0f;
    }

    /// <summary>How many enemies one slice swing hits at most: the nearest ones in the forward hemisphere.</summary>
    public const int SliceTargets = 2;

    /// <summary>Damage one slice swing deals to each enemy it hits: the item's
    /// <c>entityDamage</c>, halved on the combo's return swing. Rounded to whole hit points.</summary>
    public static int SliceDamage(Item? held, bool returnSwing, float damageFactor = 1f) =>
        (int)MathF.Round(Mining.EntityDamage(held) * damageFactor * (returnSwing ? 0.5f : 1f),
            MidpointRounding.AwayFromZero);

    /// <summary>Damage one sword swing deals to each enemy it hits: the item's <c>entityDamage</c>
    /// scaled by the swing's <see cref="SwordSwings.Power"/>. Rounded to whole hit points.</summary>
    public static int SwordDamage(Item? held, SwordSwing swing, float damageFactor = 1f) =>
        (int)MathF.Round(Mining.EntityDamage(held) * damageFactor * SwordSwings.Power(swing), MidpointRounding.AwayFromZero);

    /// <summary>A blow of <paramref name="damage"/> raised by the striker's undead bonus when the target is undead
    /// (rounded, never below the blow); unchanged otherwise.</summary>
    public static int VersusUndead(int damage, bool targetIsUndead, float undeadBonus) =>
        !targetIsUndead || undeadBonus <= 0f || damage <= 0
            ? damage
            : Math.Max(damage, (int)MathF.Round(damage * (1f + undeadBonus), MidpointRounding.AwayFromZero));
}
