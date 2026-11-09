using System;

namespace VoxelEngine.Entities.TowerMobs;

/// <summary>
/// The Skeleton King's decisions, GL-free: its phase by health share (phase 1 slams and volleys, phase 2 adds
/// summon waves, phase 3 enrages with shorter cooldowns and a faster walk), its attack cooldowns and which attack
/// it picks by range, sight and cooldown. Entering a new phase owes exactly one summon, taken before anything else;
/// the first health observation is silent, so a king restored from a save at low health sets its phase without
/// summoning. The phase only rises until <see cref="Reset"/> (a king that heals back to full). The roll source is
/// injected, so a test can drive the summon chance.
/// </summary>
/// <param name="roll">A uniform roll in 0..1, read for the summon chance.</param>
public sealed class SkeletonKingBrain(Func<float> roll)
{
    private float _slamCooldown;
    private float _volleyCooldown = MobConfig.SkeletonKingFirstVolleyDelaySeconds;
    private float _summonCooldown = MobConfig.SkeletonKingSummonCooldownSeconds;
    private bool _observed;
    private bool _summonOwed;

    /// <summary>The fight's phase, 1..3.</summary>
    public int Phase { get; private set; } = 1;

    /// <summary>Whether the king is enraged (phase 3).</summary>
    public bool IsEnraged => Phase >= 3;

    /// <summary>The factor every chosen attack's cooldown is multiplied by: shorter while enraged.</summary>
    public float CooldownScale => IsEnraged ? MobConfig.SkeletonKingEnrageCooldownFactor : 1f;

    /// <summary>The factor on the king's walk: faster while enraged.</summary>
    public float SpeedFactor => IsEnraged ? MobConfig.SkeletonKingEnrageSpeedFactor : 1f;

    /// <summary>The phase a health share (0..1) puts the king in.</summary>
    public static int PhaseOf(float healthShare) =>
        healthShare <= MobConfig.SkeletonKingPhase3Share ? 3
        : healthShare <= MobConfig.SkeletonKingPhase2Share ? 2
        : 1;

    /// <summary>
    /// Takes the king's current health share: the phase rises to the one it names (never falls); a rise after the
    /// first observation owes one summon, while the first observation only sets the phase.
    /// </summary>
    public void Observe(float healthShare)
    {
        var phase = PhaseOf(healthShare);
        if (phase > Phase)
        {
            Phase = phase;
            if (_observed) _summonOwed = true;
        }

        _observed = true;
    }

    /// <summary>Counts the attack cooldowns down by <paramref name="dt"/> seconds.</summary>
    public void Tick(float dt)
    {
        _slamCooldown = MathF.Max(0f, _slamCooldown - dt);
        _volleyCooldown = MathF.Max(0f, _volleyCooldown - dt);
        _summonCooldown = MathF.Max(0f, _summonCooldown - dt);
    }

    /// <summary>
    /// Picks one attack, first match wins: an owed summon (phase 2 on); a slam with a target in reach and the slam
    /// ready; a rolled summon from phase 2 with its cooldown over; a volley at a seen target inside the volley band
    /// while standing on the ground; else none. The chosen attack's cooldown restarts, scaled by
    /// <see cref="CooldownScale"/>.
    /// </summary>
    /// <param name="distance">The horizontal distance to the target.</param>
    /// <param name="inSlamReach">Whether the target is in slam reach.</param>
    /// <param name="seesTarget">Whether the line to the target's chest is clear.</param>
    /// <param name="grounded">Whether the king stands on the ground.</param>
    public SkeletonKingAttack Choose(float distance, bool inSlamReach, bool seesTarget, bool grounded)
    {
        if (_summonOwed && Phase >= 2)
        {
            _summonOwed = false;
            _summonCooldown = MobConfig.SkeletonKingSummonCooldownSeconds * CooldownScale;
            return SkeletonKingAttack.Summon;
        }

        if (inSlamReach && _slamCooldown <= 0f)
        {
            _slamCooldown = MobConfig.SkeletonKingSlamCooldownSeconds * CooldownScale;
            return SkeletonKingAttack.Slam;
        }

        if (Phase >= 2 && _summonCooldown <= 0f && roll() < MobConfig.SkeletonKingSummonChance)
        {
            _summonCooldown = MobConfig.SkeletonKingSummonCooldownSeconds * CooldownScale;
            return SkeletonKingAttack.Summon;
        }

        var inBand = distance >= MobConfig.SkeletonKingVolleyMinRange && distance <= MobConfig.SkeletonKingVolleyMaxRange;
        if (inBand && seesTarget && grounded && _volleyCooldown <= 0f)
        {
            _volleyCooldown = MobConfig.SkeletonKingVolleyCooldownSeconds * CooldownScale;
            return SkeletonKingAttack.Volley;
        }

        return SkeletonKingAttack.None;
    }

    /// <summary>Starts over: phase 1, fresh cooldowns, nothing owed, and the next observation is silent again.</summary>
    public void Reset()
    {
        Phase = 1;
        _slamCooldown = 0f;
        _volleyCooldown = MobConfig.SkeletonKingFirstVolleyDelaySeconds;
        _summonCooldown = MobConfig.SkeletonKingSummonCooldownSeconds;
        _observed = false;
        _summonOwed = false;
    }

    /// <summary>Sets the phase a server reported (a client's mirror, which never observes), clamped to 1..3.</summary>
    public void SetPhase(int phase) => Phase = Math.Clamp(phase, 1, 3);
}
