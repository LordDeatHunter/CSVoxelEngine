using System;

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
    private int _max = ManaConfig.StartMax;

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
    /// The progression hook: sizes the pool for <paramref name="knownParts"/> known spell parts (fed by the
    /// known parts of MG-8). A smaller pool clamps the mana down to it; a larger one never refills it.
    /// </summary>
    public void SetKnownParts(int knownParts)
    {
        _max = MaxFor(knownParts);
        Current = Math.Min(Current, _max);
    }

    /// <summary>Passive regeneration: <see cref="Max"/> / <see cref="ManaConfig.RegenFullSeconds"/> per
    /// second, never past the pool.</summary>
    public void Regen(float deltaTime) =>
        Current = Math.Min(_max, Current + Math.Max(0f, deltaTime) * _max / ManaConfig.RegenFullSeconds);

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
    /// 1..<see cref="ManaConfig.MaxCap"/>, the mana into it.</summary>
    public void SetNet(int mana, int max)
    {
        _max = Math.Clamp(max, 1, ManaConfig.MaxCap);
        Current = Math.Clamp(mana, 0, _max);
    }

    /// <summary>Save-restore seam: the saved mana clamped into the pool; a non-finite value refills it.</summary>
    public void Restore(float mana) =>
        Current = float.IsFinite(mana) ? Math.Clamp(mana, 0f, _max) : _max;

    /// <summary>A full pool again (a respawn).</summary>
    public void Refill() => Current = _max;
}
