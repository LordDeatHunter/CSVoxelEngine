using System;
using VoxelEngine.Config;
using VoxelEngine.Items;
using VoxelEngine.Status;

namespace VoxelEngine.Player;

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

    private int _maxBonus;

    /// <summary>
    /// Applies what worn gear changes: the health bonus, and (only where this pool is simulated, never on a
    /// client that mirrors its server's mana) the mana bonus and regeneration.
    /// </summary>
    public void ApplyGear(in GearStats gear, bool simulatesMana)
    {
        SetMaxBonus(gear.MaxHealth);
        if (simulatesMana)
            Mana.SetGear(gear.MaxMana, gear.ManaRegenFactor);
    }

    /// <summary>The pool's size: <see cref="MaxHealth"/> plus what worn gear adds. The hearts scale to it.</summary>
    public int Max => MaxHealth + _maxBonus;

    /// <summary>
    /// Sets what worn gear adds to the pool (clamped to 0..<see cref="GearConfig.MaxHealthBonusCap"/>). A smaller
    /// pool pulls the health down with it; a larger one leaves it where it is, so putting gear on never heals.
    /// </summary>
    public void SetMaxBonus(int bonus)
    {
        _maxBonus = Math.Clamp(bonus, 0, GearConfig.MaxHealthBonusCap);
        if (Health > Max)
            Health = Max;
    }

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
    /// with no damage flash, no partial lava tick and no status effects. A saved 0 stays 0, so the owner's first
    /// tick respawns the player instead of loading them dead. The mana is left alone: the save
    /// restore and the client mirror set it explicitly.
    /// </summary>
    public void Restore(int health)
    {
        Health = Math.Clamp(health, 0, Max);
        DamageFlash = 0f;
        _lavaTimer = 0f;
        Breath.Refill();
        Effects.Clear();
    }

    public void TakeDamage(int amount)
    {
        if (amount <= 0)
            return;
        Health = Math.Max(0, Health - amount);
        DamageFlash = 1f;
        _secondsSinceCombat = 0f;
    }

    // A fresh pool starts out of combat, so it regenerates at once
    private float _secondsSinceCombat = PlayerConfig.RegenDelaySeconds;
    private float _regenCarry;

    /// <summary>The player just landed a blow: the combat clock restarts, like damage taken.</summary>
    public void NoteCombat() => _secondsSinceCombat = 0f;

    /// <summary>
    /// Passive regeneration, call every frame: out of combat (no damage taken or dealt for
    /// <see cref="PlayerConfig.RegenDelaySeconds"/>) the pool refills at
    /// <see cref="PlayerConfig.RegenPerSecond"/>, scaled by <see cref="PlayerConfig.RegenSprintFactor"/>
    /// while <paramref name="sprinting"/>. Never past the pool, never while dead, and an
    /// <paramref name="immune"/> player (creative, flying) needs none.
    /// </summary>
    public void UpdateRegen(float deltaTime, bool sprinting, bool immune)
    {
        if (IsDead)
            return;
        _secondsSinceCombat += Math.Max(0f, deltaTime);
        if (immune || Health >= Max || _secondsSinceCombat < PlayerConfig.RegenDelaySeconds)
        {
            _regenCarry = 0f;
            return;
        }

        var rate = PlayerConfig.RegenPerSecond * (sprinting ? PlayerConfig.RegenSprintFactor : 1f);
        _regenCarry += rate * Math.Max(0f, deltaTime);
        var whole = (int)_regenCarry;
        if (whole <= 0)
            return;
        _regenCarry -= whole;
        Heal(whole);
    }

    /// <summary>The source of the last recorded blow, for the death message (see
    /// <see cref="DeathMessages"/>); <see cref="DamageSource.None"/> until something names itself.
    /// A later source-less hit leaves it standing.</summary>
    public DamageSource LastSource { get; private set; }

    /// <summary>The killer's display name when the last recorded blow was a mob's, else null.</summary>
    public string? LastKiller { get; private set; }

    /// <summary>Like <see cref="TakeDamage(int)"/>, but names what dealt the blow so the death
    /// message can say it.</summary>
    public void TakeDamage(int amount, DamageSource source, string? killer = null)
    {
        if (amount <= 0)
            return;
        if (source != DamageSource.None)
        {
            LastSource = source;
            LastKiller = killer;
        }
        TakeDamage(amount);
    }

    /// <summary>
    /// Restores up to <paramref name="amount"/> HP (a bandage), clamped to the pool. False when
    /// there is nothing to restore (already full, or dead), so a use is never spent for nothing.
    /// </summary>
    public bool Heal(int amount)
    {
        if (amount <= 0 || Health <= 0 || Health >= Max)
            return false;
        Health = Math.Min(Max, Health + amount);
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
            TakeDamage(LavaDamagePerTick, DamageSource.Lava);
        }
    }

    /// <summary>The air under water; <see cref="UpdateBreath"/> advances it.</summary>
    public Breath Breath { get; } = new();

    /// <summary>Drains the air while the eye is under water and bills a drowning blow per tick once it
    /// is empty; <paramref name="immune"/> (creative/flight) never drains.</summary>
    public void UpdateBreath(float deltaTime, bool eyeUnderWater, bool immune)
    {
        var ticks = Breath.Step(deltaTime, eyeUnderWater, immune);
        for (var i = 0; i < ticks; i++)
            TakeDamage(PlayerConfig.DrownDamagePerTick, DamageSource.Drowning);
    }

    /// <summary>The mana pool; <see cref="UpdateMana"/> regenerates it.</summary>
    public PlayerMana Mana { get; } = new();

    /// <summary>Regenerates the mana; call every frame. A dead player does not regenerate.</summary>
    public void UpdateMana(float deltaTime)
    {
        if (IsDead)
            return;
        Mana.Regen(deltaTime);
    }

    /// <summary>The status effects on the player; <see cref="UpdateEffects"/> ticks them in the
    /// authoritative simulation, and a client's copy is the server's mirror.</summary>
    public StatusEffectSet Effects { get; } = new();

    /// <summary>
    /// Ticks the status effects; call every frame in the authoritative simulation only. A dead or
    /// <paramref name="immune"/> player (creative, flight) has every effect cleared; otherwise a tick's
    /// damage lands as a <see cref="DamageSource.Magic"/> blow carrying the effect's death phrase.
    /// </summary>
    public void UpdateEffects(float deltaTime, bool immune)
    {
        if (IsDead || immune)
        {
            Effects.Clear();
            return;
        }
        var damage = Effects.Tick(deltaTime, Max);
        if (damage > 0)
            TakeDamage(damage, DamageSource.Magic, Effects.LastDamageEffect?.DeathMessage);
    }

    /// <summary>Decays <see cref="DamageFlash"/>; call every frame.</summary>
    public void Update(float deltaTime) =>
        DamageFlash = Math.Max(0f, DamageFlash - Math.Max(0f, deltaTime) / DamageFlashSeconds);

    /// <summary>Simple respawn: full pool, fresh lava timer, full air and mana, no status effects. The
    /// caller owns the teleport.</summary>
    public void Respawn()
    {
        Health = Max;
        _lavaTimer = 0f;
        Breath.Refill();
        Mana.Refill();
        Effects.Clear();
    }
}
