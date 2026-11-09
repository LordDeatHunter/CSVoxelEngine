using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Goblins;
using VoxelEngine.Entities.TowerMobs;
using VoxelEngine.Particles;

namespace VoxelEngine.Simulation.Tower;

/// <summary>
/// Owns the battle tower's <see cref="SkeletonKing"/>s and the bones they throw, on the shared realm frame
/// (<see cref="RealmMobManager{TMob}"/>): at the slam's impact it bills the shockwave to every target it reaches and
/// throws bone shrapnel all round, from the volley's release it throws a burst of bones, each strayed a little off the
/// aimed throw (both <see cref="MobMissiles"/>,
/// which hurt only targets, never the king or a skeleton), and at the summon it raises a ring of
/// skeletons of the king's tier through the <see cref="SkeletonManager"/>, never more living than
/// <see cref="MobConfig.SkeletonKingMaxMinions"/>. A king never spawns on its own: only through
/// <see cref="Spawn(Vector3, SkeletonKingHome, bool)"/> (the altar's ritual, a scenario, a save restore, a client's
/// mirror), judged by the global <see cref="MobCaps"/>, and at most one per tower. A king fades out past every
/// target's <see cref="MobConfig.SkeletonKingLeaveRadiusBlocks"/>, and with nobody in play at all (which the shared
/// despawn never judges), so the tower's ritual comes free again; <see cref="StatusFor"/> tells the altar whether
/// its king lives, was killed or left. Each frame a king knocked off its roof is put back on it. Main thread only.
/// </summary>
public sealed class SkeletonKingManager : RealmMobManager<SkeletonKing>
{
    private readonly EntityType? _type;
    // The volley bones and the slam shards in flight, each carrying its own damage
    private readonly MobMissiles _bones = new();
    private readonly byte _boneItemId;
    // The skeletons the kings summoned and still track (pruned once they are dying or dead)
    private readonly List<Skeleton> _minions = [];
    // The manager the summons go through this frame; none (no summons) when the update is given none
    private SkeletonManager? _minionManager;
    private ChunkManager? _chunks;

    /// <summary>
    /// Validates the kind's content first, so a model missing a part, clip or event fails at startup naming its
    /// file. A null type (unregistered content) runs no kings. Joins <paramref name="caps"/> (the world's), or caps
    /// itself alone when none is given (tests).
    /// </summary>
    public SkeletonKingManager(EntityType? type, MobCaps? caps = null) : base(caps)
    {
        if (type != null) SkeletonKing.ValidateContent(type);
        _type = type;
        _boneItemId = ContentRegistry.RequireItem("bone").Id;
    }

    /// <summary>Bones in flight (volley bones and slam shards); drawn and mirrored like the player's own throws.</summary>
    public IReadOnlyList<ThrownProjectile> Bones => _bones.Flying;

    /// <summary>The summoned skeletons still alive (not dying, not dead), what <see cref="MobConfig.SkeletonKingMaxMinions"/>
    /// caps.</summary>
    internal int LivingMinions
    {
        get
        {
            var count = 0;
            for (var i = 0; i < _minions.Count; i++)
                if (!_minions[i].IsDying && !_minions[i].IsDead) count++;
            return count;
        }
    }

    /// <summary>The body height of the named kind (where a scenario or the altar puts its center above the feet), or
    /// 0 for an unknown kind.</summary>
    public static float HeightOf(string kind) =>
        string.Equals(kind, SkeletonKing.EntityName, StringComparison.OrdinalIgnoreCase) ? MobConfig.SkeletonKingHeight : 0f;

    /// <summary>
    /// The strongest status among the kings bound to tower <paramref name="tower"/>, in the order
    /// <see cref="KingStatus.Alive"/>, <see cref="KingStatus.Killed"/>, <see cref="KingStatus.Fading"/>,
    /// <see cref="KingStatus.None"/>. A kill reads <see cref="KingStatus.Killed"/> the moment its health hits 0,
    /// before the next update starts its death. An unbound king never counts for any tower.
    /// </summary>
    public KingStatus StatusFor((int CellX, int CellZ) tower)
    {
        var best = KingStatus.None;
        var mobs = Mobs;
        for (var i = 0; i < mobs.Count; i++)
        {
            var king = mobs[i];
            if (king.Home.Tower != tower) continue;
            var status = StatusOf(king);
            if (Rank(status) > Rank(best)) best = status;
        }
        return best;
    }

    /// <summary>
    /// The only way a king appears: a forced spawn at an exact body-center position, bound to
    /// <paramref name="home"/>, which may pass the natural cap but never the hard cap. Null when the kind is not
    /// registered, the hard cap is full, or the home's tower already has a king alive or killed.
    /// </summary>
    public SkeletonKing? Spawn(Vector3 center, SkeletonKingHome home, bool spawnIn = true)
    {
        if (_type == null || Caps.ForcedRoom <= 0) return null;
        if (home.Tower is { } tower && StatusFor(tower) is KingStatus.Alive or KingStatus.Killed) return null;
        return Track(new SkeletonKing(_type, center, home, spawnIn));
    }

    /// <summary>A client's mirror of a server king: unbound (a mirror never leashes or reports to a tower) and
    /// already risen.</summary>
    public SkeletonKing? SpawnMirror(Vector3 center) => Spawn(center, SkeletonKingHome.Unbound(FeetOf(center)), false);

    /// <inheritdoc />
    protected override SkeletonKing? Create(string kind, Vector3 position, bool spawnIn) =>
        _type != null && string.Equals(kind, SkeletonKing.EntityName, StringComparison.OrdinalIgnoreCase)
            ? new SkeletonKing(_type, position, SkeletonKingHome.Unbound(FeetOf(position)), spawnIn)
            : null;

    /// <summary>
    /// Ticks every king and bone against <paramref name="targets"/>. With nobody in play every living king fades
    /// out (no loot, health untouched), so the tower's ritual comes free again; a king knocked off its roof is put
    /// back. Summons raise skeletons through <paramref name="minions"/>, or none without it. Spawns no king.
    /// </summary>
    public void Update(float deltaTime, ChunkManager chunks, DropManager drops, IReadOnlyList<IMobTarget> targets,
        BlockDebrisSystem? debris = null, SkeletonManager? minions = null)
    {
        if (chunks == null || targets == null) return;
        _minionManager = minions;
        _chunks = chunks;

        // The shared despawn never judges an empty target list: let every king go here
        if (targets.Count == 0) FadeAllKings();

        UpdateMobs(deltaTime, chunks, drops, targets, debris, MobConfig.SkeletonKingLeaveRadiusBlocks);

        var mobs = Mobs;
        for (var i = 0; i < mobs.Count; i++)
            if (!mobs[i].IsDying && !mobs[i].IsDead) mobs[i].EnforceLeash();

        _minions.RemoveAll(static m => m.IsDying || m.IsDead);

        // A bone or shard flies under gravity; any target it touches stops it, and the first non-immune one takes its
        // damage (mobs never stop it, so the king and the skeletons are never hit)
        _bones.Update(deltaTime, chunks, targets);
        var hits = _bones.Hits;
        for (var i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            Hit(hit.Target, hit.Damage, hit.Position, MobConfig.SkeletonKingBoneKnockbackSpeed, 0f, _type?.DisplayName);
        }

        _minionManager = null;
        _chunks = null;
    }

    /// <summary>Test seam: raises one summon wave from <paramref name="king"/> as if its summon event fired.</summary>
    internal void TestSummon(SkeletonKing king, SkeletonManager minions, ChunkManager chunks) =>
        Summon(king, minions, chunks);

    /// <summary>Test seam: launches one slam shard from <paramref name="origin"/> along <paramref name="velocity"/>,
    /// exactly as a slam's burst does.</summary>
    internal ThrownProjectile TestLaunchShard(Vector3 origin, Vector3 velocity) => LaunchShard(origin, velocity);

    // A slam's shockwave and shards land at its impact, a volley's burst bones one after another from its release, a
    // summon's skeletons at its event
    /// <inheritdoc />
    protected override void Attack(SkeletonKing mob, IReadOnlyList<IMobTarget> targets)
    {
        while (mob.TryTakeBurstBone(out var origin, out var aim))
            BurstBone(origin, aim);

        if (mob.TryTakeSummon())
        {
            if (_minionManager != null && _chunks != null) Summon(mob, _minionManager, _chunks);
            return;
        }

        if (!mob.TryTakeSlamImpact()) return;
        var feet = mob.Feet;
        for (var t = 0; t < targets.Count; t++)
        {
            var target = targets[t];
            if (target.IsImmune || !SkeletonKing.InShockwave(feet, target.Eye)) continue;
            Hit(target, MobConfig.SkeletonKingSlamDamage, mob.Position, MobConfig.SkeletonKingSlamKnockbackSpeed,
                MobConfig.SkeletonKingSlamKnockbackLift, mob.Type.DisplayName);
        }
        SlamShrapnel(feet);
    }

    // The slam's bone shrapnel: a burst all round, flying like the bone blade's (BoneShrapnel.RollFragment). The
    // shards are mob missiles, which only ever touch targets, so they never hurt the king or a skeleton
    private void SlamShrapnel(Vector3 feet)
    {
        var center = feet + new Vector3(0f, MobConfig.SkeletonKingSlamShrapnelLiftBlocks, 0f);
        for (var i = 0; i < MobConfig.SkeletonKingSlamShrapnelCount; i++)
        {
            var (origin, velocity) = BoneShrapnel.RollFragment(center);
            LaunchShard(origin, velocity);
        }
    }

    private ThrownProjectile LaunchShard(Vector3 origin, Vector3 velocity) =>
        _bones.Launch(_boneItemId, origin, velocity, MobConfig.SkeletonKingSlamShrapnelDamage,
            ShrapnelConfig.GravityScale, ShrapnelConfig.LifeSeconds);

    /// <inheritdoc />
    protected override float ShoveSpeedOf(SkeletonKing mob) => MobConfig.SkeletonKingShoveSpeed;

    // One bone of a burst: the aimed throw strayed at random within the burst's spread, pitch and speed jitter
    private void BurstBone(Vector3 origin, Vector3 aim)
    {
        GoblinAim.TryLaunchVelocity(origin, aim, MobConfig.SkeletonKingVolleySpeed, ThrownProjectile.Gravity,
            out var velocity);
        const float toRadians = MathF.PI / 180f;
        var velocityOut = SkeletonKing.ScatterVelocity(velocity,
            Signed() * MobConfig.SkeletonKingBurstSpreadDegrees * toRadians,
            Signed() * MobConfig.SkeletonKingBurstPitchDegrees * toRadians,
            1f + Signed() * MobConfig.SkeletonKingBurstSpeedJitter);
        _bones.Launch(_boneItemId, origin, velocityOut, MobConfig.SkeletonKingVolleyDamage);
    }

    // Uniform in [-1, 1)
    private static float Signed() => Random.Shared.NextSingle() * 2f - 1f;

    // Raises up to SummonCount skeletons of the king's tier on a ring around its feet, never past the minion cap or
    // the hard cap; a ring spot needs solid ground under two free body cells (an unloaded cell refuses it)
    private void Summon(SkeletonKing king, SkeletonManager minions, ChunkManager chunks)
    {
        _minions.RemoveAll(static m => m.IsDying || m.IsDead);
        var wanted = Math.Min(MobConfig.SkeletonKingSummonCount, MobConfig.SkeletonKingMaxMinions - _minions.Count);
        if (wanted <= 0) return;

        var feet = king.Feet;
        // Twice as many spots as skeletons, so a blocked spot leaves others to try
        var spots = MobConfig.SkeletonKingSummonCount * 2;
        var phase = king.Facing;
        for (var s = 0; s < spots && wanted > 0; s++)
        {
            var angle = phase + MathF.Tau * s / spots;
            var (sin, cos) = MathF.SinCos(angle);
            var at = new Vector3(feet.X + cos * MobConfig.SkeletonKingSummonRadius, feet.Y,
                feet.Z + sin * MobConfig.SkeletonKingSummonRadius);
            if (!TryStandingSpot(chunks, ref at)) continue;

            var skeleton = minions.Spawn(at + new Vector3(0f, MobConfig.SkeletonHeight * 0.5f, 0f), king.Home.SummonTier);
            if (skeleton == null) return;
            _minions.Add(skeleton);
            wanted--;
        }
    }

    // A ring spot at the king's feet level, or a step below or above it: a king standing on the altar block has its
    // feet a cell over the roof the skeletons rise on
    private static bool TryStandingSpot(ChunkManager chunks, ref Vector3 feet)
    {
        ReadOnlySpan<float> steps = [0f, -1f, 1f];
        foreach (var step in steps)
        {
            var at = feet with { Y = MathF.Floor(feet.Y) + step };
            if (!IsStandingSpot(chunks, at)) continue;
            feet = at;
            return true;
        }
        return false;
    }

    private static bool IsStandingSpot(ChunkManager chunks, Vector3 feet)
    {
        var x = (int)MathF.Floor(feet.X);
        var y = (int)MathF.Floor(feet.Y);
        var z = (int)MathF.Floor(feet.Z);
        return chunks.TryGetCell(x, y - 1, z, out var ground, out _) && BlockType.IsSolid(ground)
               && chunks.TryGetCell(x, y, z, out var body, out _) && !BlockType.IsSolid(body)
               && chunks.TryGetCell(x, y + 1, z, out var head, out _) && !BlockType.IsSolid(head);
    }

    // Nobody in play: every living king fades out with its health left, so it reads as gone, never as killed
    private void FadeAllKings()
    {
        var mobs = Mobs;
        for (var i = 0; i < mobs.Count; i++)
            if (!mobs[i].IsDying && !mobs[i].IsDead) mobs[i].BeginDying();
    }

    private static KingStatus StatusOf(SkeletonKing king) =>
        king.IsDead ? KingStatus.Killed : king.IsDying ? KingStatus.Fading : KingStatus.Alive;

    private static int Rank(KingStatus status) => status switch
    {
        KingStatus.Alive => 3,
        KingStatus.Killed => 2,
        KingStatus.Fading => 1,
        _ => 0,
    };

    private static Vector3 FeetOf(Vector3 center) => center - new Vector3(0f, MobConfig.SkeletonKingHeight * 0.5f, 0f);
}
