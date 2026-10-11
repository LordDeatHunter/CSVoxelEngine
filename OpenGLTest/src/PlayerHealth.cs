using System;

namespace silkgltriangle;

/// <summary>
/// The player's survival pool, 0-100 HP drawn as 10 hearts (1 heart = 10 HP).
/// Pure data and arithmetic, no GL: <see cref="World"/> feeds it fall landings and lava
/// contact every frame, and the HUD reads <see cref="Health"/> / <see cref="DamageFlash"/>.
/// </summary>
public sealed class PlayerHealth
{
    public const int MaxHealth = 100;
    public const int HeartCount = 10;
    public const int HpPerHeart = 10;

    /// <summary>Minimum fall height in blocks that hurts; anything shorter lands free.</summary>
    public const float FallMinBlocks = 5f;

    /// <summary>Lava burns 5 HP every half second, i.e. 10 HP/s.</summary>
    public const float LavaTickSeconds = 0.5f;
    public const int LavaDamagePerTick = 5;

    /// <summary>How long the damage vignette takes to fade after a hit.</summary>
    public const float DamageFlashSeconds = 1.5f;

    public int Health { get; private set; } = MaxHealth;

    /// <summary>
    /// Damage feedback strength, 1 on a hit decaying to 0. Drives the screen-corner
    /// reddening; the screen shake is fired separately through <see cref="Camera.AddShake"/>.
    /// </summary>
    public float DamageFlash { get; private set; }

    public bool IsDead => Health <= 0;

    private float _lavaTimer;

    /// <summary>
    /// Fall damage for a landing: nothing below <see cref="FallMinBlocks"/>, then 10 HP per
    /// block past the 4-block grace (5 blocks = 10 HP = 1 heart, 13 blocks = 90 HP),
    /// clamped to the whole pool. Takes the raw drop distance; callers own the threshold
    /// epsilon and the water/creative/flying immunity.
    /// </summary>
    public static int FallDamage(float dropBlocks)
    {
        if (dropBlocks < FallMinBlocks - 1e-3f)
            return 0;
        var blocks = (int)MathF.Floor(dropBlocks + 1e-3f);
        return Math.Clamp((blocks - 4) * HpPerHeart, 0, MaxHealth);
    }

    /// <summary>
    /// Save-restore seam: sets the pool to a saved value, clamped to 0..<see cref="MaxHealth"/>,
    /// with no damage flash and no partial lava tick. A saved 0 stays 0, so the owner's first
    /// tick respawns the player instead of loading them dead.
    /// </summary>
    public void Restore(int health)
    {
        Health = Math.Clamp(health, 0, MaxHealth);
        DamageFlash = 0f;
        _lavaTimer = 0f;
    }

    public void TakeDamage(int amount)
    {
        if (amount <= 0)
            return;
        Health = Math.Max(0, Health - amount);
        DamageFlash = 1f;
    }

    /// <summary>
    /// Restores up to <paramref name="amount"/> HP (a bandage), clamped to the pool. False when
    /// there is nothing to restore (already full, or dead), so a use is never spent for nothing.
    /// </summary>
    public bool Heal(int amount)
    {
        if (amount <= 0 || Health <= 0 || Health >= MaxHealth)
            return false;
        Health = Math.Min(MaxHealth, Health + amount);
        return true;
    }

    /// <summary>Burns while <paramref name="inLava"/> and not <paramref name="immune"/>
    /// (creative/flight). Leaving the lava (or going immune) clears the partial tick, so
    /// re-entry never eats an instant hit.</summary>
    public void UpdateLava(float deltaTime, bool inLava, bool immune)
    {
        if (immune || !inLava)
        {
            _lavaTimer = 0f;
            return;
        }

        _lavaTimer += Math.Max(0f, deltaTime);
        while (_lavaTimer >= LavaTickSeconds)
        {
            _lavaTimer -= LavaTickSeconds;
            TakeDamage(LavaDamagePerTick);
        }
    }

    /// <summary>Decays <see cref="DamageFlash"/>; call every frame.</summary>
    public void Update(float deltaTime) =>
        DamageFlash = Math.Max(0f, DamageFlash - Math.Max(0f, deltaTime) / DamageFlashSeconds);

    /// <summary>Simple respawn: full pool, fresh lava timer. The caller owns the teleport.</summary>
    public void Respawn()
    {
        Health = MaxHealth;
        _lavaTimer = 0f;
    }
}
