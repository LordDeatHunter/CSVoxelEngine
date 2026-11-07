using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.Entities;
using VoxelEngine.Entities.HellMobs;
using VoxelEngine.Particles;
using VoxelEngine.Player;
using VoxelEngine.Status;
using VoxelEngine.WorldGen.Hell;

namespace VoxelEngine.Simulation;

/// <summary>
/// Owns hell's mobs (<see cref="Hellbrute"/>s and <see cref="Cinderwitch"/>es) and the fire bolts the
/// casters throw: on the shared realm frame (<see cref="RealmMobManager{TMob}"/>) it bills a brute's blow at
/// its melee impact to every target still in reach, launches a bolt at each cast release, burns a target a
/// bolt touches (damage plus the <c>burning</c> status effect) and spawns new mobs only on open cavern floors
/// in hell (<see cref="MobSpawner.TryFindHellSpawn"/>) around a target standing in hell, a pack whose size
/// grows with the layer (<see cref="LayerDifficulty.PackSize"/>). Every spawn is judged by the global
/// <see cref="MobCaps"/> it shares with the other mob kinds. Main thread only.
/// </summary>
public sealed class HellMobManager : RealmMobManager<HellMob>
{
    private const string BurningEffect = "burning";

    private readonly EntityType? _bruteType;
    private readonly EntityType? _casterType;
    private readonly List<ThrownProjectile> _bolts = [];
    // Parallel to _bolts: the damage each bolt carries (its caster's, scaled by the caster's layer)
    private readonly List<int> _boltDamage = [];
    private readonly byte _boltItemId;

    /// <summary>
    /// Validates both kinds' content first, so a model missing a part, clip or event fails at startup
    /// naming its file. Either type may be null (unregistered content), which leaves that kind out.
    /// Joins <paramref name="caps"/> (the world's), or caps itself alone when none is given (tests).
    /// </summary>
    public HellMobManager(EntityType? bruteType, EntityType? casterType, MobCaps? caps = null) : base(caps)
    {
        if (bruteType != null) Hellbrute.ValidateContent(bruteType);
        if (casterType != null) Cinderwitch.ValidateContent(casterType);
        _bruteType = bruteType;
        _casterType = casterType;
        _boltItemId = ContentRegistry.RequireItem("ember_shard").Id;
    }

    /// <summary>Fire bolts in flight; drawn and mirrored like the player's own throws.</summary>
    public IReadOnlyList<ThrownProjectile> Bolts => _bolts;

    /// <summary>The body height of the named kind (where a scenario or a ring spawn puts its center above
    /// the feet), or 0 for an unknown kind.</summary>
    public static float HeightOf(string kind) =>
        string.Equals(kind, Hellbrute.EntityName, StringComparison.OrdinalIgnoreCase) ? MobConfig.HellbruteHeight
        : string.Equals(kind, Cinderwitch.EntityName, StringComparison.OrdinalIgnoreCase) ? MobConfig.HellCasterHeight
        : 0f;

    /// <inheritdoc />
    protected override HellMob? Create(string kind, Vector3 position, bool spawnIn)
    {
        if (_bruteType != null && string.Equals(kind, Hellbrute.EntityName, StringComparison.OrdinalIgnoreCase))
            return new Hellbrute(_bruteType, position, spawnIn);
        if (_casterType != null && string.Equals(kind, Cinderwitch.EntityName, StringComparison.OrdinalIgnoreCase))
            return new Cinderwitch(_casterType, position, spawnIn);
        return null;
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
        if (chunks == null || targets == null) return;

        UpdateMobs(deltaTime, chunks, drops, targets, debris, MobConfig.HellDespawnRadiusBlocks);
        UpdateBolts(deltaTime, chunks, targets, embers);
        UpdateSpawning(deltaTime, chunks, targets);
    }

    // A brute's blow lands on every non-immune target still in reach at the clip's impact; a caster's
    // release launches its bolt
    /// <inheritdoc />
    protected override void Attack(HellMob mob, IReadOnlyList<IMobTarget> targets)
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
            if (target.IsImmune || !Hellbrute.InMeleeReach(brute.Position, target.Eye)) continue;
            Hit(target, damage, brute.Position, MobConfig.HellbruteKnockbackSpeed, MobConfig.HellbruteKnockbackLift,
                brute.Type.DisplayName);
        }
    }

    /// <inheritdoc />
    protected override float ShoveSpeedOf(HellMob mob) =>
        mob is Hellbrute ? MobConfig.HellbruteShoveSpeed : MobConfig.HellCasterShoveSpeed;

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

            if (hurt != null)
            {
                Hit(hurt, _boltDamage[i], bolt.Position, MobConfig.HellBoltKnockbackSpeed, 0f, _casterType?.DisplayName);
                StatusEffects.Apply(hurt, BurningEffect, MobConfig.HellBoltBurnScale);
            }
            if (!touched && alive) continue;
            _bolts.RemoveAt(i);
            _boltDamage.RemoveAt(i);
        }
    }

    // Each turn of the shared timer tries the ring round the next target in turn, and only a target
    // standing in hell is searched around.
    private void UpdateSpawning(float deltaTime, ChunkManager chunks, IReadOnlyList<IMobTarget> targets)
    {
        if (_bruteType == null && _casterType == null) return;
        if (!TryTakeSpawnTurn(deltaTime, targets, MobConfig.HellSpawnIntervalSeconds, out var target)) return;

        var eye = target.Eye;
        if (!HellGenerator.InHell(MathUtils.FloorToInt(eye.Y - Camera.EyeHeight))) return;

        if (!MobSpawner.TryFindHellSpawn(chunks, eye, MobConfig.HellSpawnMinRadiusBlocks,
                MobConfig.HellSpawnMaxRadiusBlocks, MobConfig.HellSpawnTries, MobConfig.HellSpawnSearchHalfHeight,
                MobConfig.HellSpawnScanDepth, out var x, out var groundY, out var z))
            return;

        var pack = 1 + Random.Shared.Next(LayerDifficulty.PackSize(HellGenerator.LayerIndex(groundY)));
        SpawnOnFloor(x, groundY, z);
        for (var member = 1; member < pack && Caps.AllowsNaturalSpawn; member++)
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
