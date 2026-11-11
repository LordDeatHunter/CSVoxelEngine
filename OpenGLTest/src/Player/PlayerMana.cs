using System;
using VoxelEngine.Config;

namespace VoxelEngine.Player;

/// <summary>
/// The player's mana, GL-free: it refills slowly and steadily (<see cref="ManaConfig.RegenFullSeconds"/>
/// from empty), a cast spends from it (<see cref="TrySpend"/>, free in creative, refused when short) and its
/// max grows with the spell parts the player knows (<see cref="SetKnownParts"/>). Owned by
/// <see cref="PlayerHealth"/>, which regenerates it (<see cref="PlayerHealth.UpdateMana"/>). Server-owned:
/// singleplayer and a server simulate it, a client only mirrors the server's status (<see cref="SetNet"/>).
/// </summary>
public sealed class PlayerMana
{
    private int _baseMax = ManaConfig.StartMax;
    private int _bonus;
    private float _regenFactor = 1f;
    private int _max = ManaConfig.StartMax;

    /// <summary>The largest pool the wire carries: the progression cap plus the most gear can add.</summary>
    public const int WireMaxCap = ManaConfig.MaxCap + GearConfig.MaxManaBonusCap;

    /// <summary>The size of the pool, at least 1.</summary>
    public int Max => _max;

    /// <summary>Mana left, 0..<see cref="Max"/>.</summary>
    public float Current { get; private set; } = ManaConfig.StartMax;

    /// <summary>The mana as a share of the pool, 0..1.</summary>
    public float Fraction => Current / _max;

    /// <summary>The pool of a player who knows <paramref name="knownParts"/> spell parts: it grows by
    /// <see cref="ManaConfig.MaxPerKnownPart"/> each, up to <see cref="ManaConfig.MaxCap"/>.</summary>
    public static int MaxFor(int knownParts) =>
        Math.Min(ManaConfig.MaxCap, ManaConfig.StartMax + Math.Max(0, knownParts) * ManaConfig.MaxPerKnownPart);

    /// <summary>
    /// The progression hook: sizes the pool for <paramref name="knownParts"/> known spell parts (fed by
    /// <see cref="Magic.KnownSpellParts.SizeMana"/>). A smaller pool clamps the mana down to it; a larger one
    /// never refills it.
    /// </summary>
    public void SetKnownParts(int knownParts)
    {
        _baseMax = MaxFor(knownParts);
        Resize();
    }

    /// <summary>
    /// What worn gear adds: <paramref name="maxBonus"/> to the pool (0..<see cref="GearConfig.MaxManaBonusCap"/>)
    /// and a <paramref name="regenFactor"/> on its regeneration. Only the authoritative simulation calls it; a
    /// client mirrors the finished max (<see cref="SetNet"/>). A smaller pool clamps the mana down, a larger
    /// one never refills it.
    /// </summary>
    public void SetGear(int maxBonus, float regenFactor)
    {
        _bonus = Math.Clamp(maxBonus, 0, GearConfig.MaxManaBonusCap);
        _regenFactor = float.IsFinite(regenFactor) ? Math.Max(0.1f, regenFactor) : 1f;
        Resize();
    }

    private void Resize()
    {
        _max = _baseMax + _bonus;
        Current = Math.Min(Current, _max);
    }

    /// <summary>Passive regeneration: <see cref="Max"/> / <see cref="ManaConfig.RegenFullSeconds"/> per
    /// second (scaled by worn gear), never past the pool.</summary>
    public void Regen(float deltaTime) =>
        Current = Math.Min(_max, Current + Math.Max(0f, deltaTime) * _max * _regenFactor / ManaConfig.RegenFullSeconds);

    /// <summary>
    /// Restores <paramref name="share"/> (0..1) of the pool, never past it (a mana item, MG-9). Refused,
    /// changing nothing, when the pool is already full or the share is not a positive finite number, so
    /// an item is never spent for nothing.
    /// </summary>
    public bool TryRestore(float share)
    {
        if (!float.IsFinite(share) || share <= 0f || Current >= _max)
            return false;
        Current = Math.Min(_max, Current + share * _max);
        return true;
    }

    /// <summary>
    /// Spends <paramref name="cost"/> for a cast (MG-4 calls it). A <paramref name="creative"/> player casts
    /// for free and keeps the pool; a cost the pool cannot cover is refused and changes nothing, so a cast
    /// is never half paid. A non-finite cost is refused, and a cost of zero or less is a free success.
    /// </summary>
    public bool TrySpend(float cost, bool creative)
    {
        if (!float.IsFinite(cost))
            return false;
        if (creative || cost <= 0f)
            return true;
        if (Current < cost)
            return false;
        Current -= cost;
        return true;
    }

    /// <summary>The mana in whole units on the wire, rounded down so a mirror never shows mana that
    /// cannot be spent.</summary>
    public int NetMana => Math.Clamp((int)MathF.Floor(Current + 1e-4f), 0, _max);

    /// <summary>Sets the pool from the wire (a client mirroring its server): the max clamped to
    /// 1..<see cref="WireMaxCap"/>, the mana into it.</summary>
    public void SetNet(int mana, int max)
    {
        _max = Math.Clamp(max, 1, WireMaxCap);
        Current = Math.Clamp(mana, 0, _max);
    }

    /// <summary>Save-restore seam: the saved mana clamped into the pool; a non-finite value refills it.</summary>
    public void Restore(float mana) =>
        Current = float.IsFinite(mana) ? Math.Clamp(mana, 0f, _max) : _max;

    /// <summary>A full pool again (a respawn).</summary>
    public void Refill() => Current = _max;

    /// <summary>An empty pool (a waystone teleport spends all of it); it regenerates from there as usual.</summary>
    public void Drain() => Current = 0f;

    /// <summary>Whether the pool is strictly more than half full, judged on the whole-unit <see cref="NetMana"/> a
    /// server and its client agree on (what a waystone teleport needs).</summary>
    public bool MoreThanHalf => NetMana * 2 > _max;
}
