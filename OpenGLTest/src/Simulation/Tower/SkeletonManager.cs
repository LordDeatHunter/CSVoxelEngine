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
/// Owns the battle tower's <see cref="Skeleton"/>s and the arrows they shoot: on the shared realm frame
/// (<see cref="RealmMobManager{TMob}"/>) it bills a rake at its melee impact to every target still in reach,
/// launches an arrow (a <see cref="ThrownProjectile"/> of the <c>arrow</c> item, which flies point first) at each
/// bow release and hurts the first non-immune target an arrow touches. Skeletons never spawn on their own: they
/// appear only through <see cref="Spawn(Vector3, int, bool)"/>, judged by the global <see cref="MobCaps"/> it
/// shares with the other mob kinds. A skeleton bound to a tower floor (<see cref="Skeleton.Floor"/>) despawns like
/// any mob, and so does one the tower leashes back (<see cref="Release"/>): each is reported once through
/// <see cref="TakeReleased"/>, so its floor owes it back and raises it again later. Leaving a tower therefore never
/// clears a floor and never leaves skeletons holding cap room far away. Main thread only.
/// </summary>
public sealed class SkeletonManager : RealmMobManager<Skeleton>
{
    private readonly EntityType? _type;
    // The floors of bound skeletons that despawned or were released since the last TakeReleased
    private readonly List<TowerFloorRef> _released = [];
    // Each arrow carries its archer's damage, scaled by the archer's tier
    private readonly MobMissiles _arrows = new();
    private readonly byte _arrowItemId;

    /// <summary>
    /// Validates the kind's content first, so a model missing a part, clip or event fails at startup naming its
    /// file. A null type (unregistered content) runs no skeletons. Joins <paramref name="caps"/> (the world's), or
    /// caps itself alone when none is given (tests).
    /// </summary>
    public SkeletonManager(EntityType? type, MobCaps? caps = null) : base(caps)
    {
        if (type != null) Skeleton.ValidateContent(type);
        _type = type;
        _arrowItemId = ContentRegistry.RequireItem("flint_arrow").Id;
    }

    /// <summary>Arrows in flight; drawn and mirrored like the player's own throws.</summary>
    public IReadOnlyList<ThrownProjectile> Arrows => _arrows.Flying;

    /// <summary>The body height of the named kind (where a scenario or a pile puts its center above the feet), or
    /// 0 for an unknown kind.</summary>
    public static float HeightOf(string kind) =>
        string.Equals(kind, Skeleton.EntityName, StringComparison.OrdinalIgnoreCase) ? MobConfig.SkeletonHeight : 0f;

    /// <summary>
    /// The only way a skeleton appears (a scenario, a save restore, a client's mirror, a BT-4 bone pile): a forced
    /// spawn at an exact body-center position on tower floor <paramref name="tier"/> (clamped), which may pass the
    /// natural cap but never the hard cap. Skeletons never spawn naturally. Null when the kind is not registered or
    /// the hard cap is full.
    /// </summary>
    public Skeleton? Spawn(Vector3 position, int tier, bool spawnIn = true)
    {
        if (_type == null || Caps.ForcedRoom <= 0) return null;
        return Track(new Skeleton(_type, position, tier, spawnIn));
    }

    /// <inheritdoc />
    protected override Skeleton? Create(string kind, Vector3 position, bool spawnIn) =>
        _type != null && string.Equals(kind, Skeleton.EntityName, StringComparison.OrdinalIgnoreCase)
            ? new Skeleton(_type, position, 0, spawnIn)
            : null;

    /// <summary>
    /// Fades out <paramref name="skeleton"/> without loot (a tower leash: it strayed too far from its floor) and,
    /// when it is bound to a floor, reports that floor through <see cref="TakeReleased"/>. A dying or dead skeleton
    /// is left alone, so a skeleton is never reported twice.
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
            Hit(target, damage, mob.Position, MobConfig.SkeletonMeleeKnockbackSpeed, MobConfig.SkeletonMeleeKnockbackLift,
                mob.Type.DisplayName);
        }
    }

    /// <inheritdoc />
    protected override float ShoveSpeedOf(Skeleton mob) => MobConfig.SkeletonShoveSpeed;
}
