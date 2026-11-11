using System;
using VoxelEngine.Config;

namespace VoxelEngine.Items;

/// <summary>
/// One hit's bone-shrapnel payload: how many fragments a landed melee hit sprays and the damage each
/// deals. A blade carries its own profile in its JSON <c>shrapnel</c> object; the bone charm grants
/// <see cref="GearAbility.BoneShrapnel"/>, which adds a weaker default profile to any weapon's hit, and
/// sharpens a blade's own by <see cref="ShrapnelConfig.TogetherFactor"/> when both are present.
/// </summary>
/// <param name="Count">Fragments sprayed per landed hit.</param>
/// <param name="Damage">Damage each fragment deals to an enemy it touches.</param>
public readonly record struct ShrapnelProfile(int Count, int Damage)
{
    /// <summary>Whether this profile sprays nothing.</summary>
    public bool IsEmpty => Count <= 0 || Damage <= 0;

    /// <summary>
    /// The profile a landed hit with <paramref name="weapon"/> sprays for a striker whose worn gear does
    /// or does not grant <see cref="GearAbility.BoneShrapnel"/>: the blade's own profile, the charm's
    /// weaker default one, or (both) the blade's raised by <see cref="ShrapnelConfig.TogetherFactor"/>.
    /// Empty when neither is present.
    /// </summary>
    public static ShrapnelProfile Resolve(Item? weapon, bool charmWorn)
    {
        if (weapon?.Shrapnel is { } blade)
            return charmWorn ? Raised(blade) : blade;
        return charmWorn ? new ShrapnelProfile(ShrapnelConfig.CharmCount, ShrapnelConfig.CharmDamage) : default;
    }

    // Rounds away from zero and never down to nothing, so the charm always adds something.
    private static ShrapnelProfile Raised(ShrapnelProfile blade) =>
        new(Math.Max(1, (int)MathF.Round(blade.Count * ShrapnelConfig.TogetherFactor, MidpointRounding.AwayFromZero)),
            Math.Max(1, (int)MathF.Round(blade.Damage * ShrapnelConfig.TogetherFactor, MidpointRounding.AwayFromZero)));
}
