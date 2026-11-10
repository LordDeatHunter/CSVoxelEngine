using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.Entities;
using VoxelEngine.Entities.TowerMobs;
using VoxelEngine.Particles;

namespace VoxelEngine.Simulation.Tower;

/// <summary>
/// Owns the battle tower's <see cref="Skeleton"/>s, archers and warriors, and the arrows they shoot: on the shared realm
/// frame (<see cref="RealmMobManager{TMob}"/>) it bills a melee blow at its impact to every target still in reach,
/// launches an arrow (a <see cref="ThrownProjectile"/> of the <c>arrow</c> item, which flies point first) at each
/// bow release and hurts the first non-immune target an arrow touches. Skeletons never spawn on their own: they
/// appear only through <see cref="Spawn(Vector3, int, bool)"/> (which alternates archers and warriors, so a tower's
/// waves are half each, and rolls each one's outfit), judged by the global <see cref="MobCaps"/> it
/// shares with the other mob kinds. A skeleton bound to a tower floor (<see cref="Skeleton.Floor"/>) despawns like
/// any mob, and so does one the tower leashes back (<see cref="Release"/>): each is reported once through
/// <see cref="TakeReleased"/>, so its floor owes it back and raises it again later; so does one that chased nobody
/// for <see cref="MobConfig.SkeletonPileReturnIdleSeconds"/>, which crumbles back into its piles and rises again at
/// full health. Leaving a tower therefore never clears a floor and never leaves skeletons holding cap room far away.
/// Main thread, authoritative worlds only (a client's mirrors never tick).
/// </summary>
public sealed class SkeletonManager : RealmMobManager<Skeleton>
{
    private readonly EntityType? _type;
    private readonly EntityType? _warriorType;
    // The role the next ordinary spawn takes: they alternate, so every wave is half archers, half warriors
    private SkeletonRole _nextRole = SkeletonRole.Archer;
    // The floors of bound skeletons that despawned or were released since the last TakeReleased
    private readonly List<TowerFloorRef> _released = [];
    // Each arrow carries its archer's damage, scaled by the archer's tier
    private readonly MobMissiles _arrows = new();
    private readonly ushort _arrowItemId;

    /// <summary>
    /// Validates both kinds' content first, so a model missing a part, clip or event fails at startup naming its
    /// file. A null archer type (unregistered content) runs no skeletons; the warrior type defaults to the registered
    /// <see cref="Skeleton.WarriorEntityName"/>, and without one every skeleton is an archer. Joins
    /// <paramref name="caps"/> (the world's), or caps itself alone when none is given (tests).
    /// </summary>
    public SkeletonManager(EntityType? type, MobCaps? caps = null, EntityType? warriorType = null) : base(caps)
    {
        if (type != null) Skeleton.ValidateContent(type);
        _type = type;
        _warriorType = type == null ? null : warriorType ?? EntityRegistry.TryGet(Skeleton.WarriorEntityName);
        if (_warriorType != null) Skeleton.ValidateContent(_warriorType);
        _arrowItemId = ContentRegistry.RequireItem("flint_arrow").Id;
    }

    /// <summary>How many living (not dying) skeletons stand within <paramref name="radius"/> blocks of
    /// <paramref name="center"/>.</summary>
    public int LivingCountNear(Vector3 center, float radius)
    {
        var radiusSquared = radius * radius;
        var count = 0;
        var mobs = Mobs;
        for (var i = 0; i < mobs.Count; i++)
            if (!mobs[i].IsDying && Vector3.DistanceSquared(mobs[i].Position, center) <= radiusSquared) count++;
        return count;
    }

    /// <summary>Arrows in flight; drawn and mirrored like the player's own throws.</summary>
    public IReadOnlyList<ThrownProjectile> Arrows => _arrows.Flying;

    /// <summary>The body height of the named kind, an archer or a warrior (where a scenario or a pile puts its center
    /// above the feet), or 0 for an unknown kind.</summary>
    public static float HeightOf(string kind) =>
        string.Equals(kind, Skeleton.EntityName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, Skeleton.WarriorEntityName, StringComparison.OrdinalIgnoreCase)
            ? MobConfig.SkeletonHeight
            : 0f;

    /// <summary>
    /// The only way a new skeleton appears (a scenario, a BT-4 bone pile, the Skeleton King's summons): a forced spawn
    /// at an exact body-center position on tower floor <paramref name="tier"/> (clamped), which may pass the natural cap
    /// but never the hard cap. Skeletons never spawn naturally. Archers and warriors take turns, and each rolls its
    /// outfit (<see cref="SkeletonOutfit.Roll"/>). Null when the kind is not registered or the hard cap is full.
    /// </summary>
    public Skeleton? Spawn(Vector3 position, int tier, bool spawnIn = true)
    {
        var role = _warriorType == null ? SkeletonRole.Archer : _nextRole;
        var skeleton = Spawn(position, tier, role,
            SkeletonOutfit.Roll(role, Random.Shared.NextSingle(), Random.Shared.NextSingle()), spawnIn);
        if (skeleton != null) _nextRole = role == SkeletonRole.Archer ? SkeletonRole.Warrior : SkeletonRole.Archer;
        return skeleton;
    }

    /// <summary>A skeleton of a known <paramref name="role"/> and <paramref name="outfit"/> (a save restore, a client's
    /// mirror, a scenario naming the kind), under the same caps as <see cref="Spawn(Vector3, int, bool)"/>. A warrior
    /// with no warrior kind registered comes back as an archer.</summary>
    public Skeleton? Spawn(Vector3 position, int tier, SkeletonRole role, SkeletonOutfit outfit, bool spawnIn = true)
    {
        if (_type == null || Caps.ForcedRoom <= 0) return null;
        if (role == SkeletonRole.Warrior && _warriorType == null) role = SkeletonRole.Archer;
        var type = role == SkeletonRole.Warrior ? _warriorType! : _type;
        return Track(new Skeleton(type, position, tier, spawnIn, role, outfit));
    }

    /// <inheritdoc />
    protected override Skeleton? Create(string kind, Vector3 position, bool spawnIn)
    {
        if (_type == null || HeightOf(kind) <= 0f) return null;
        var role = Skeleton.RoleOf(kind);
        if (role == SkeletonRole.Warrior && _warriorType == null) return null;
        return new Skeleton(role == SkeletonRole.Warrior ? _warriorType! : _type, position, 0, spawnIn, role);
    }

    /// <summary>
    /// Fades out <paramref name="skeleton"/> without loot (a tower leash: it strayed too far from its floor; or it
    /// idled long enough to crumble back into its piles, <see cref="Skeleton.ReturnsToPile"/>) and, when it is bound
    /// to a floor, reports that floor through <see cref="TakeReleased"/>. A dying or dead skeleton is left alone, so a
    /// skeleton is never reported twice.
    /// </summary>
    internal void Release(Skeleton skeleton)
    {
        if (skeleton.IsDying || skeleton.IsDead) return;
        if (skeleton.Floor is { } floor) _released.Add(floor);
        skeleton.BeginDying();
    }

    /// <summary>Appends the floors of the bound skeletons that despawned or were released since the last call to
    /// <paramref name="into"/>, then forgets them.</summary>
    internal void TakeReleased(List<TowerFloorRef> into)
    {
        into.AddRange(_released);
        _released.Clear();
    }

    /// <inheritdoc />
    protected override void OnDespawn(Skeleton mob)
    {
        if (mob.Floor is { } floor) _released.Add(floor);
    }

    /// <summary>Test seam: launches an arrow as if a skeleton had shot it, carrying <paramref name="damage"/>.</summary>
    internal ThrownProjectile TestLaunchArrow(Vector3 origin, Vector3 velocity, int damage) =>
        _arrows.Launch(_arrowItemId, origin, velocity, damage);

    /// <summary>
    /// Ticks every skeleton and arrow against <paramref name="targets"/> (each skeleton chasing its noticed
    /// target, <see cref="Mob.PickTarget"/>). Spawns nothing.
    /// </summary>
    public void Update(float deltaTime, ChunkManager chunks, DropManager drops, IReadOnlyList<IMobTarget> targets,
        BlockDebrisSystem? debris = null)
    {
        if (chunks == null || targets == null) return;

        UpdateMobs(deltaTime, chunks, drops, targets, debris, MobConfig.SkeletonDespawnRadiusBlocks);
        ReturnIdleToPiles();

        // An arrow flies under gravity; any target it touches stops it, and the first non-immune one takes its damage
        _arrows.Update(deltaTime, chunks, targets);
        var hits = _arrows.Hits;
        for (var i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            Hit(hit.Target, hit.Damage, hit.Position, MobConfig.SkeletonArrowKnockbackSpeed, 0f, _type?.DisplayName);
        }
    }

    // A release launches the arrow; a rake lands on every non-immune target still in reach at the clip's impact
    /// <inheritdoc />
    protected override void Attack(Skeleton mob, IReadOnlyList<IMobTarget> targets)
    {
        if (mob.TryTakeShotRelease(out var origin, out var velocity))
        {
            _arrows.Launch(_arrowItemId, origin, velocity, mob.ArrowDamage);
            return;
        }

        if (!mob.TryTakeMeleeImpact()) return;
        var damage = mob.MeleeDamage;
        for (var t = 0; t < targets.Count; t++)
        {
            var target = targets[t];
            if (target.IsImmune || !Skeleton.InMeleeReach(mob.Position, target.Eye)) continue;
            Hit(target, damage, mob.Position, mob.MeleeKnockbackSpeed, MobConfig.SkeletonMeleeKnockbackLift,
                mob.Type.DisplayName);
        }
    }

    /// <inheritdoc />
    protected override float ShoveSpeedOf(Skeleton mob) => MobConfig.SkeletonShoveSpeed;

    // A bound skeleton that has chased nobody for too long crumbles back into its floor's piles: released like a
    // leashed one, so the floor owes it and its piles raise it again, at full health, for the next target
    private void ReturnIdleToPiles()
    {
        var mobs = Mobs;
        for (var i = 0; i < mobs.Count; i++)
        {
            var skeleton = mobs[i];
            if (skeleton.ReturnsToPile) Release(skeleton);
        }
    }
}
