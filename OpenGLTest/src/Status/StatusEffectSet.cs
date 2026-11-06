using System;
using System.Numerics;
using VoxelEngine.Content.StatusEffects;

namespace VoxelEngine.Status;

/// <summary>
/// The status effects active on one target (a mob, the player's pool), GL-free and main thread only. An
/// application refreshes an effect's remaining time to the longer of the two and never stacks its damage;
/// <see cref="Tick"/> runs the damage-over-time and gathers the block actions, and only the authoritative
/// simulation calls it. A client's mirror takes the server's mask through <see cref="SetMirrorMask"/>
/// and never ticks. The arrays are allocated on the first <see cref="Apply"/> (or a non-empty mirror
/// mask), so an untouched target costs nothing and nothing allocates afterwards.
/// </summary>
public sealed class StatusEffectSet
{
    private StatusEffect?[]? _active;
    private float[]? _remaining;
    private float[]? _tickTimer;
    private StatusEffectBlockAction _pending;

    // Set by SetMirrorMask: a mirror has no timers, so Tick must leave it alone
    private bool _isMirror;

    /// <summary>The active effects as a one-byte mask of their <see cref="StatusEffect.Bit"/>s.</summary>
    public byte Mask { get; private set; }

    /// <summary>The movement speed factor: the lowest of the active effects', 1 when none slows.</summary>
    public float MoveFactor { get; private set; } = 1f;

    /// <summary>The attack pace factor: the lowest of the active effects', 1 when none slows.</summary>
    public float AttackFactor { get; private set; } = 1f;

    /// <summary>The effect whose tick last dealt damage, for the death message; null until one has.</summary>
    public StatusEffect? LastDamageEffect { get; private set; }

    /// <summary>
    /// Applies <paramref name="effect"/> for its duration times <paramref name="durationScale"/> (clamped
    /// to 0..<see cref="StatusEffectConfig.MaxDurationScale"/>). An effect already active keeps its tick
    /// clock and takes the longer of the two remaining times, so re-applying refreshes rather than stacks.
    /// False when the scaled duration is not positive.
    /// </summary>
    public bool Apply(StatusEffect effect, float durationScale)
    {
        var duration = effect.DurationSeconds * Math.Clamp(durationScale, 0f, StatusEffectConfig.MaxDurationScale);
        if (!(duration > 0f))
            return false;
        EnsureArrays();
        _isMirror = false;
        var id = effect.Id;
        if (_active![id] == null)
        {
            _active[id] = effect;
            _remaining![id] = 0f;
            _tickTimer![id] = 0f;
        }
        _remaining![id] = Math.Max(_remaining[id], duration);
        Recompute();
        return true;
    }

    /// <summary>
    /// Advances every active effect by <paramref name="deltaTime"/>: a tick lands each time its clock
    /// passes the effect's <see cref="StatusEffect.TickSeconds"/>, but only inside the remaining duration;
    /// a tick ORs the effect's block actions into the pending set and, for a damaging effect, adds
    /// <see cref="DamagePerTick"/> of <paramref name="maxHealth"/>. An effect whose time runs out is
    /// removed with its partial tick. Returns the damage dealt this call. A mirror never ticks.
    /// </summary>
    public int Tick(float deltaTime, int maxHealth)
    {
        if (_isMirror || Mask == 0 || _active == null || _remaining == null || _tickTimer == null)
            return 0;
        var dt = Math.Max(0f, deltaTime);
        var damage = 0;
        var changed = false;
        for (var id = 0; id < StatusEffect.MaxKinds; id++)
        {
            var effect = _active[id];
            if (effect == null)
                continue;

            _tickTimer[id] += Math.Min(dt, _remaining[id]);
            if (effect.TickSeconds > 0f)
            {
                while (_tickTimer[id] >= effect.TickSeconds)
                {
                    _tickTimer[id] -= effect.TickSeconds;
                    _pending |= effect.BlockActions;
                    if (effect.DamageShare <= 0f)
                        continue;
                    damage += DamagePerTick(effect.DamageShare, maxHealth);
                    LastDamageEffect = effect;
                }
            }

            _remaining[id] -= dt;
            if (_remaining[id] > 0f)
                continue;
            _active[id] = null;
            _remaining[id] = 0f;
            _tickTimer[id] = 0f;
            changed = true;
        }
        if (changed)
            Recompute();
        return damage;
    }

    /// <summary>One tick's damage: <paramref name="share"/> of <paramref name="maxHealth"/>, rounded, at least 1.</summary>
    public static int DamagePerTick(float share, int maxHealth) =>
        Math.Max(1, (int)MathF.Round(share * maxHealth));

    /// <summary>The block actions gathered by the ticks since the last call, cleared.</summary>
    public StatusEffectBlockAction TakeBlockActions()
    {
        var pending = _pending;
        _pending = StatusEffectBlockAction.None;
        return pending;
    }

    /// <summary>Removes every effect, its timers and the pending block actions.</summary>
    public void Clear()
    {
        if (_active != null)
        {
            Array.Clear(_active);
            Array.Clear(_remaining!);
            Array.Clear(_tickTimer!);
        }
        _pending = StatusEffectBlockAction.None;
        LastDamageEffect = null;
        Recompute();
    }

    /// <summary>
    /// A client mirror's update: the active set becomes the registered effects in
    /// <paramref name="mask"/> (an unknown bit is dropped). No timer runs on a mirror: <see cref="Tick"/>
    /// leaves it alone until an <see cref="Apply"/> makes it authoritative again.
    /// </summary>
    public void SetMirrorMask(byte mask)
    {
        _isMirror = true;
        mask &= StatusEffectRegistry.KnownMask;
        if (mask == Mask)
            return;
        if (mask != 0)
            EnsureArrays();
        if (_active != null)
        {
            for (var id = 0; id < StatusEffect.MaxKinds; id++)
                _active[id] = (mask & (1 << id)) != 0 ? StatusEffectRegistry.ById(id) : null;
        }
        Recompute();
    }

    /// <summary>The tint of the lowest-id active effect that has one, in linear light.</summary>
    public bool TryGetTint(out Vector3 linear)
    {
        if (_active != null)
        {
            for (var id = 0; id < StatusEffect.MaxKinds; id++)
            {
                if (_active[id]?.TintLinear is not { } tint)
                    continue;
                linear = tint;
                return true;
            }
        }
        linear = default;
        return false;
    }

    /// <summary>Whether <paramref name="effect"/> is active.</summary>
    public bool IsActive(StatusEffect effect) => (Mask & effect.Bit) != 0;

    /// <summary>The seconds <paramref name="effect"/> has left; 0 when inactive (and on a mirror).</summary>
    public float RemainingSeconds(StatusEffect effect) =>
        IsActive(effect) && _remaining != null ? Math.Max(0f, _remaining[effect.Id]) : 0f;

    private void EnsureArrays()
    {
        if (_active != null)
            return;
        _active = new StatusEffect?[StatusEffect.MaxKinds];
        _remaining = new float[StatusEffect.MaxKinds];
        _tickTimer = new float[StatusEffect.MaxKinds];
    }

    private void Recompute()
    {
        byte mask = 0;
        var move = 1f;
        var attack = 1f;
        if (_active != null)
        {
            foreach (var effect in _active)
            {
                if (effect == null)
                    continue;
                mask |= effect.Bit;
                move = Math.Min(move, effect.MoveFactor);
                attack = Math.Min(attack, effect.AttackFactor);
            }
        }
        Mask = mask;
        MoveFactor = move;
        AttackFactor = attack;
    }
}
