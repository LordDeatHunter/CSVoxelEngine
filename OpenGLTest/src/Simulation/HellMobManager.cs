using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.Content.Loot;
using VoxelEngine.Entities;
using VoxelEngine.Entities.HellMobs;
using VoxelEngine.Particles;
using VoxelEngine.Player;
using VoxelEngine.Status;
using VoxelEngine.WorldGen.Hell;

namespace VoxelEngine.Simulation;

/// <summary>
/// Owns hell's mobs (<see cref="Hellbrute"/>s and <see cref="Cinderwitch"/>es) and the fire bolts the
/// casters throw: ticks their AI and physics, bills a brute's blow at its melee impact to every target
/// still in reach, launches a bolt at each cast release, burns a target a bolt touches (damage plus the
/// <c>burning</c> status effect), shoves a mob a player walks into, rolls death loot, despawns by
/// distance and spawns new ones only on open cavern floors in hell (<see cref="MobSpawner.TryFindHellSpawn"/>)
/// around a target standing in hell, a pack whose size grows with the layer
/// (<see cref="LayerDifficulty.PackSize"/>). Every spawn is judged by the global <see cref="MobCaps"/>
/// it shares with the other mob kinds. Main thread only.
/// </summary>
public sealed class HellMobManager : IMobPopulation
{
    private const string BurningEffect = "burning";

    private readonly EntityType? _bruteType;
    private readonly EntityType? _casterType;
    private readonly MobCaps _caps;
    private readonly List<HellMob> _mobs = [];
    private readonly List<ThrownProjectile> _bolts = [];
    // Parallel to _bolts: the damage each bolt carries (its caster's, scaled by the caster's layer)
    private readonly List<int> _boltDamage = [];
    private readonly List<byte> _loot = [];
    private readonly byte _boltItemId;
    private float _spawnTimer;
    private int _spawnCursor;

    /// <summary>
    /// Validates both kinds' content first, so a model missing a part, clip or event fails at startup
    /// naming its file. Either type may be null (unregistered content), which leaves that kind out.
    /// Joins <paramref name="caps"/> (the world's), or caps itself alone when none is given (tests).
    /// </summary>
    public HellMobManager(EntityType? bruteType, EntityType? casterType, MobCaps? caps = null)
    {
        if (bruteType != null) Hellbrute.ValidateContent(bruteType);
        if (casterType != null) Cinderwitch.ValidateContent(casterType);
        _bruteType = bruteType;
        _casterType = casterType;
        _boltItemId = ContentRegistry.RequireItem("ember_shard").Id;
        _caps = caps ?? new MobCaps();
        _caps.Register(this);
    }

    /// <summary>Every hell mob still drawn: the living ones plus any playing their death.</summary>
    public IReadOnlyList<HellMob> Mobs => _mobs;

    /// <summary>Fire bolts in flight; drawn and mirrored like the player's own throws.</summary>
    public IReadOnlyList<ThrownProjectile> Bolts => _bolts;

    /// <summary>Hell mobs that are alive (not yet dying), what the global caps count.</summary>
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

    /// <summary>Health the hell mobs took off every target together during the most recent
    /// <see cref="Update"/>; each hit also reached its target through <see cref="IMobTarget.TakeHit"/>.</summary>
    public int DamageDealtThisFrame { get; private set; }

    /// <summary>
    /// Adds a mob of the named kind (<see cref="Hellbrute.EntityName"/> or <see cref="Cinderwitch.EntityName"/>)
    /// at an exact body-center position: a forced spawn (scenarios, save restores, a client's mirror, tests),
    /// which may pass the natural cap but never the hard cap. Null for an unknown or unregistered kind or a full
    /// hard cap. Its layer, and so its health, comes from the position.
    /// </summary>
    public HellMob? Spawn(string kind, Vector3 position, bool spawnIn = true)
    {
        if (_caps.ForcedRoom <= 0) return null;
        HellMob? mob = null;
        if (_bruteType != null && string.Equals(kind, Hellbrute.EntityName, StringComparison.OrdinalIgnoreCase))
            mob = new Hellbrute(_bruteType, position, spawnIn);
        else if (_casterType != null && string.Equals(kind, Cinderwitch.EntityName, StringComparison.OrdinalIgnoreCase))
            mob = new Cinderwitch(_casterType, position, spawnIn);
        if (mob != null) _mobs.Add(mob);
        return mob;
    }

    /// <summary>The body height of the named kind (where a scenario or a ring spawn puts its center above
    /// the feet), or 0 for an unknown kind.</summary>
    public static float HeightOf(string kind) =>
        string.Equals(kind, Hellbrute.EntityName, StringComparison.OrdinalIgnoreCase) ? MobConfig.HellbruteHeight
        : string.Equals(kind, Cinderwitch.EntityName, StringComparison.OrdinalIgnoreCase) ? MobConfig.HellCasterHeight
        : 0f;

    /// <summary>A client's network reconcile: the server no longer reports this mob, so it plays its
    /// death where it stands and is then dropped.</summary>
    public bool Remove(HellMob mob)
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

    /// <summary>Test seam: launches a bolt as if a caster had thrown it, carrying <paramref name="damage"/>.</summary>
    internal ThrownProjectile TestLaunchBolt(Vector3 origin, Vector3 velocity, int damage)
    {
        var bolt = new ThrownProjectile(_boltItemId, origin, velocity, 0f, 1f);
        _bolts.Add(bolt);
        _boltDamage.Add(damage);
        return bolt;
    }

    /// <summary>
    /// Ticks every hell mob and bolt against <paramref name="targets"/> (each mob chasing its noticed
    /// target, <see cref="Mob.PickTarget"/>), then the ring spawner. <paramref name="embers"/>, when given
    /// (never on a server), sheds a trail of embers off each bolt in flight.
    /// </summary>
    public void Update(float deltaTime, ChunkManager chunks, DropManager drops, IReadOnlyList<IMobTarget> targets,
        BlockDebrisSystem? debris = null, FireParticleSystem? embers = null)
    {
        DamageDealtThisFrame = 0;
        if (chunks == null || targets == null) return;

        const float despawnSquared = MobConfig.HellDespawnRadiusBlocks * MobConfig.HellDespawnRadiusBlocks;
        for (var i = _mobs.Count - 1; i >= 0; i--)
        {
            var mob = _mobs[i];
            var chased = mob.PickTarget(targets, deltaTime, chunks);
            mob.Update(deltaTime, chunks, chased >= 0 && !mob.IsDying ? targets[chased] : null,
                chased < 0 ? mob.WanderGoal(targets.Count, deltaTime) : null);
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
                mob.BeginDying();
        }

        UpdateBolts(deltaTime, chunks, targets, embers);
        UpdateSpawning(deltaTime, chunks, targets);
    }

    // A brute's blow lands on every non-immune target still in reach at the clip's impact; a caster's
    // release launches its bolt
    private void Attack(HellMob mob, IReadOnlyList<IMobTarget> targets)
    {
        if (mob is Cinderwitch caster && caster.TryTakeCastRelease(out var origin, out var velocity))
        {
            _bolts.Add(new ThrownProjectile(_boltItemId, origin, velocity, 0f, 1f));
            _boltDamage.Add(caster.BoltDamage);
            return;
        }

        if (mob is not Hellbrute brute || !brute.TryTakeMeleeImpact()) return;
        var damage = brute.MeleeDamage;
        for (var t = 0; t < targets.Count; t++)
        {
            var target = targets[t];
            var eye = target.Eye;
            if (target.IsImmune || !Hellbrute.InMeleeReach(brute.Position, eye)) continue;
            DamageDealtThisFrame += damage;
            var away = new Vector3(eye.X - brute.Position.X, 0f, eye.Z - brute.Position.Z);
            target.TakeHit(damage, away, MobConfig.HellbruteKnockbackSpeed, MobConfig.HellbruteKnockbackLift,
                brute.Type.DisplayName);
        }
    }

    // Walking into a grounded hell mob is harmless: the player shoves it (a brute barely moves)
    private static void ShoveOffTargets(HellMob mob, IReadOnlyList<IMobTarget> targets)
    {
        if (!mob.IsGrounded) return;
        var speed = mob is Hellbrute ? MobConfig.HellbruteShoveSpeed : MobConfig.HellCasterShoveSpeed;
        for (var t = 0; t < targets.Count; t++)
        {
            var eye = targets[t].Eye;
            if (!MobTargets.BodyTouches(eye, mob.Position, mob.BodyHalfExtent)) continue;
            mob.Shove(new Vector3(mob.Position.X - eye.X, 0f, mob.Position.Z - eye.Z), speed);
        }
    }

    // A bolt flies under gravity; any target it touches stops it, and the first non-immune one takes
    // its damage and catches fire. A bolt that strikes a block or outlives its flight burns out.
    private void UpdateBolts(float deltaTime, ChunkManager chunks, IReadOnlyList<IMobTarget> targets,
        FireParticleSystem? embers)
    {
        for (var i = _bolts.Count - 1; i >= 0; i--)
        {
            var bolt = _bolts[i];
            var alive = bolt.Update(deltaTime, chunks, null, out _);
            if (embers != null && Random.Shared.NextSingle() < MobConfig.HellBoltEmbersPerSecond * deltaTime)
                embers.Puff(bolt.Position, 1, ThrownProjectile.HalfExtent * 2f, FireParticleKind.Ember);

            var touched = false;
            IMobTarget? hurt = null;
            for (var t = 0; t < targets.Count; t++)
            {
                var target = targets[t];
                if (!MobTargets.BodyTouches(target.Eye, bolt.Position, ThrownProjectile.HalfExtent)) continue;
                touched = true;
                if (target.IsImmune) continue;
                hurt = target;
                break;
            }

            if (hurt != null) Burn(hurt, bolt.Position, _boltDamage[i]);
            if (!touched && alive) continue;
            _bolts.RemoveAt(i);
            _boltDamage.RemoveAt(i);
        }
    }

    private void Burn(IMobTarget target, Vector3 boltPosition, int damage)
    {
        DamageDealtThisFrame += damage;
        var eye = target.Eye;
        var away = new Vector3(eye.X - boltPosition.X, 0f, eye.Z - boltPosition.Z);
        target.TakeHit(damage, away, MobConfig.HellBoltKnockbackSpeed, 0f, _casterType?.DisplayName);
        StatusEffects.Apply(target, BurningEffect, MobConfig.HellBoltBurnScale);
    }

    private void DropLoot(HellMob mob, DropManager drops)
    {
        if (mob.Type.LootTable == null) return;
        _loot.Clear();
        var (x, y, z) = mob.Position.BlockCell();
        mob.Type.LootTable.Roll(new LootContext(x, y, z, 0), _loot);
        foreach (var id in _loot)
            drops.SpawnAt(id, mob.Position, MobLoot.RandomToss());
    }

    // One shared timer under the natural cap every mob kind shares; each expiry tries the ring round the
    // next target in turn, and only a target standing in hell is searched around.
    private void UpdateSpawning(float deltaTime, ChunkManager chunks, IReadOnlyList<IMobTarget> targets)
    {
        if (!_caps.AllowsNaturalSpawn || (_bruteType == null && _casterType == null)) return;

        _spawnTimer += deltaTime;
        if (_spawnTimer < MobConfig.HellSpawnIntervalSeconds) return;
        _spawnTimer = 0f;

        if (targets.Count == 0) return;
        var index = _spawnCursor % targets.Count;
        _spawnCursor = index + 1;
        var eye = targets[index].Eye;
        if (!HellGenerator.InHell(MathUtils.FloorToInt(eye.Y - Camera.EyeHeight))) return;

        if (!MobSpawner.TryFindHellSpawn(chunks, eye, MobConfig.HellSpawnMinRadiusBlocks,
                MobConfig.HellSpawnMaxRadiusBlocks, MobConfig.HellSpawnTries, MobConfig.HellSpawnSearchHalfHeight,
                MobConfig.HellSpawnScanDepth, out var x, out var groundY, out var z))
            return;

        var pack = 1 + Random.Shared.Next(LayerDifficulty.PackSize(HellGenerator.LayerIndex(groundY)));
        SpawnOnFloor(x, groundY, z);
        for (var member = 1; member < pack && _caps.AllowsNaturalSpawn; member++)
        {
            if (MobSpawner.TryFindHellSpawn(chunks, new Vector3(x + 0.5f, groundY + 1 + Camera.EyeHeight, z + 0.5f),
                    1f, MobConfig.HellPackSpawnRadiusBlocks, MobConfig.HellSpawnTries, 2, 6,
                    out var mx, out var my, out var mz))
                SpawnOnFloor(mx, my, mz);
        }
    }

    // One ring spawn on floor cell (x, y, z): a caster with HellCasterShare, else a brute (whichever
    // kinds are registered)
    private void SpawnOnFloor(int x, int y, int z)
    {
        var caster = _casterType != null && (_bruteType == null || Random.Shared.NextSingle() < MobConfig.HellCasterShare);
        var kind = caster ? Cinderwitch.EntityName : Hellbrute.EntityName;
        Spawn(kind, new Vector3(x + 0.5f, y + 1 + HeightOf(kind) * 0.5f, z + 0.5f));
    }
}
