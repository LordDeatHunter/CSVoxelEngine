using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Loot;
using VoxelEngine.Entities;
using VoxelEngine.Particles;

namespace VoxelEngine.Simulation;

/// <summary>
/// What the realm mob managers (<see cref="HellMobManager"/>, <see cref="SkyMobManager"/>) share: the mob list
/// judged by the global <see cref="MobCaps"/>, the forced spawn, a client's death-on-removal and mirror clocks,
/// and the per-frame loop over the mobs (each chasing its noticed target, <see cref="Mob.PickTarget"/>, or
/// wandering; hazards; loot on death; the kind's attack; the shove off a player walking into it; the despawn by
/// distance or idleness), plus the one round-robin spawn timer. A subclass builds its kinds, lands their attacks
/// and runs its own ring search, or skips ring spawning altogether (a kind raised only by forced spawns).
/// Main thread only.
/// </summary>
public abstract class RealmMobManager<TMob> : IMobPopulation where TMob : RealmMob
{
    private readonly List<TMob> _mobs = [];
    private readonly List<ushort> _loot = [];
    private float _spawnTimer;
    private int _spawnCursor;

    /// <summary>Joins <paramref name="caps"/> (the world's), or caps itself alone when none is given (tests).</summary>
    protected RealmMobManager(MobCaps? caps)
    {
        Caps = caps ?? new MobCaps();
        Caps.Register(this);
    }

    /// <summary>The global caps every mob kind shares.</summary>
    protected MobCaps Caps { get; }

    /// <summary>Every mob still drawn: the living ones plus any playing their death.</summary>
    public IReadOnlyList<TMob> Mobs => _mobs;

    /// <summary>Mobs that are alive (not yet dying), what the global caps count.</summary>
    public int LivingCount
    {
        get
        {
            var count = 0;
            for (var i = 0; i < _mobs.Count; i++)
                if (!_mobs[i].IsDying) count++;
            return count;
        }
    }

    /// <summary>Health the mobs took off every target together during the most recent update; each hit also
    /// reached its target through <see cref="IMobTarget.TakeHit"/>.</summary>
    public int DamageDealtThisFrame { get; private set; }

    /// <summary>
    /// Adds a mob of the named kind at an exact body-center position: a forced spawn (scenarios, save restores,
    /// a client's mirror, tests), which may pass the natural cap but never the hard cap. Null for an unknown or
    /// unregistered kind or a full hard cap. Its difficulty comes from the position.
    /// </summary>
    public TMob? Spawn(string kind, Vector3 position, bool spawnIn = true)
    {
        if (Caps.ForcedRoom <= 0) return null;
        var mob = Create(kind, position, spawnIn);
        return mob == null ? null : Track(mob);
    }

    /// <summary>Adds an already built <paramref name="mob"/> to the list (a subclass's own spawn, which has
    /// checked the caps itself) and returns it.</summary>
    protected TMob Track(TMob mob)
    {
        _mobs.Add(mob);
        return mob;
    }

    /// <summary>A new mob of the named kind, or null for a kind this manager does not run.</summary>
    protected abstract TMob? Create(string kind, Vector3 position, bool spawnIn);

    /// <summary>A client's network reconcile: the server no longer reports this mob, so it plays its
    /// death where it stands and is then dropped.</summary>
    public bool Remove(TMob mob)
    {
        if (!_mobs.Contains(mob)) return false;
        mob.BeginDying();
        return true;
    }

    /// <summary>Advances the death and spawn clocks of the mobs without simulating them (a client never
    /// ticks the AI), dropping the ones whose death is over, with their skin-colored burst.</summary>
    public void AnimateMirrors(float deltaTime, BlockDebrisSystem? debris = null)
    {
        for (var i = _mobs.Count - 1; i >= 0; i--)
        {
            _mobs[i].AnimateMirror(deltaTime);
            if (!_mobs[i].IsRemovable) continue;
            if (debris != null) GroundDust.DeathPoof(_mobs[i], debris);
            _mobs.RemoveAt(i);
        }
    }

    /// <summary>
    /// The shared frame: resets <see cref="DamageDealtThisFrame"/>, then ticks every mob against
    /// <paramref name="targets"/>, rolls the loot of the dead, lands the kinds' attacks (<see cref="Attack"/>),
    /// lets a player shove a grounded one aside and fades out a mob past every target's
    /// <paramref name="despawnRadius"/> or idle long and far off.
    /// </summary>
    protected void UpdateMobs(float deltaTime, ChunkManager chunks, DropManager drops,
        IReadOnlyList<IMobTarget> targets, BlockDebrisSystem? debris, float despawnRadius)
    {
        DamageDealtThisFrame = 0;
        FrameDelta = deltaTime;
        var despawnSquared = despawnRadius * despawnRadius;
        for (var i = _mobs.Count - 1; i >= 0; i--)
        {
            var mob = _mobs[i];
            var chased = mob.PickTarget(targets, deltaTime, chunks);
            mob.Update(deltaTime, chunks, chased >= 0 && !mob.IsDying ? targets[chased] : null,
                chased < 0 ? mob.WanderGoal(targets.Count, deltaTime, chunks) : null);
            mob.UpdateHazards(deltaTime, chunks);

            if (mob.IsDying)
            {
                if (mob.IsRemovable)
                {
                    if (debris != null) GroundDust.DeathPoof(mob, debris);
                    _mobs.RemoveAt(i);
                }
                continue;
            }

            if (mob.IsDead)
            {
                DropLoot(mob, drops);
                mob.BeginDying();
                continue;
            }

            Attack(mob, targets);
            ShoveOffTargets(mob, targets);

            // Past every target's despawn radius, or idle long and far off: fades out, dropping nothing
            if (targets.Count > 0 && (MobTargets.MinHorizontalDistanceSquared(targets, mob.Position) > despawnSquared
                                      || mob.ShouldIdleDespawn(targets)))
            {
                OnDespawn(mob);
                mob.BeginDying();
            }
        }
    }

    /// <summary>Called once for a living mob that fades out for distance or idleness (never for a death), just
    /// before it starts dying. Does nothing by default.</summary>
    protected virtual void OnDespawn(TMob mob) { }

    /// <summary>Lands whatever attack <paramref name="mob"/>'s clip released this frame (at its timeline
    /// event), billing each hit through <see cref="Hit"/>.</summary>
    protected abstract void Attack(TMob mob, IReadOnlyList<IMobTarget> targets);

    /// <summary>The seconds of the frame <see cref="UpdateMobs"/> is running, for attacks that last longer than
    /// their impact event (a raptor's carry).</summary>
    protected float FrameDelta { get; private set; }

    /// <summary>How fast a player walking into a grounded <paramref name="mob"/> shoves it aside.</summary>
    protected abstract float ShoveSpeedOf(TMob mob);

    /// <summary>One hit of <paramref name="damage"/> on <paramref name="target"/>, shoved horizontally away from
    /// <paramref name="from"/>, counted into <see cref="DamageDealtThisFrame"/>.</summary>
    protected void Hit(IMobTarget target, int damage, Vector3 from, float knockbackSpeed, float lift, string? attacker)
    {
        DamageDealtThisFrame += damage;
        var eye = target.Eye;
        target.TakeHit(damage, new Vector3(eye.X - from.X, 0f, eye.Z - from.Z), knockbackSpeed, lift, attacker);
    }

    /// <summary>
    /// The one shared spawn timer under the natural cap every mob kind shares: true once per
    /// <paramref name="intervalSeconds"/> while the cap allows, handing out the next target in turn (round-robin),
    /// whose surroundings the subclass then searches.
    /// </summary>
    protected bool TryTakeSpawnTurn(float deltaTime, IReadOnlyList<IMobTarget> targets, float intervalSeconds,
        out IMobTarget target)
    {
        target = null!;
        if (!Caps.AllowsNaturalSpawn) return false;

        _spawnTimer += deltaTime;
        if (_spawnTimer < intervalSeconds) return false;
        _spawnTimer = 0f;

        if (targets.Count == 0) return false;
        var index = _spawnCursor % targets.Count;
        _spawnCursor = index + 1;
        target = targets[index];
        return true;
    }

    // Walking into a grounded realm mob is harmless: the player shoves it (a heavy one barely moves)
    private void ShoveOffTargets(TMob mob, IReadOnlyList<IMobTarget> targets)
    {
        if (!mob.IsGrounded) return;
        var speed = ShoveSpeedOf(mob);
        for (var t = 0; t < targets.Count; t++)
        {
            var eye = targets[t].Eye;
            if (!MobTargets.BodyTouches(eye, mob.Position, mob.BodyHalfExtent)) continue;
            mob.Shove(new Vector3(mob.Position.X - eye.X, 0f, mob.Position.Z - eye.Z), speed);
        }
    }

    private void DropLoot(TMob mob, DropManager drops)
    {
        if (mob.Type.LootTable == null) return;
        _loot.Clear();
        var (x, y, z) = mob.Position.BlockCell();
        mob.Type.LootTable.Roll(new LootContext(x, y, z, 0), _loot);
        foreach (var id in _loot)
            drops.SpawnAt(id, mob.Position, MobLoot.RandomToss());
    }
}
