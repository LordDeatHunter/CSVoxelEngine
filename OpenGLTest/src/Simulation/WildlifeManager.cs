using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Wildlife;
using VoxelEngine.Interaction;
using VoxelEngine.Particles;
using VoxelEngine.Status;

namespace VoxelEngine.Simulation;

/// <summary>
/// Owns the animals of the wild (<see cref="Animal"/>: <see cref="Deer"/>, <see cref="Boar"/>,
/// <see cref="Crow"/>, <see cref="Dragonfly"/>): on the shared realm frame (<see cref="RealmMobManager{TMob}"/>)
/// each animal reacts only to the players it heeds (<see cref="Animal.Heeds"/>) and wanders otherwise, an attack
/// is billed at its impact to every target it still reaches (<see cref="Animal.TryTakeStrike"/>), and nothing
/// despawns for standing idle. It spawns herds and flocks on dry surface around the players (a flyer in the open
/// air over its column)
/// (<see cref="MobSpawner.TryFindSurfaceSpawn(ChunkManager, SpawnOrigin, float, float, int, out int, out int, out int)"/>),
/// the species picked for the column's biome (<see cref="WildlifeSpawnRules"/>), under the wildlife budget
/// (<see cref="MobCaps.WildlifeCap"/>) that stands beside the hostile mobs' cap. Main thread only.
/// </summary>
public sealed class WildlifeManager : RealmMobManager<Animal>
{
    // The registered species' content by WildlifeKind; null where a species' entity is not registered
    private readonly EntityType?[] _types = new EntityType?[WildlifeSpecies.Names.Length];

    /// <summary>
    /// Takes every wild animal among <paramref name="types"/>, validating its content first so a model missing a
    /// part, clip or event fails at startup naming its file; any other entity is ignored. Joins
    /// <paramref name="caps"/> (the world's), or caps itself alone when none is given (tests).
    /// </summary>
    public WildlifeManager(IEnumerable<EntityType> types, MobCaps? caps = null) : base(caps)
    {
        foreach (var type in types)
        {
            if (WildlifeSpecies.KindOf(type.Name) is not { } kind) continue;
            WildlifeSpecies.ValidateContent(type);
            _types[(int)kind] = type;
        }
    }

    /// <inheritdoc />
    public override bool IsWildlife => true;

    /// <inheritdoc />
    protected override bool SpawnAllowed => Caps.AllowsWildlifeSpawn;

    /// <inheritdoc />
    protected override float SpawnHaste => Caps.WildlifeSpawnHaste;

    /// <inheritdoc />
    protected override bool DespawnsWhenIdle => false;

    /// <inheritdoc />
    protected override int PickTarget(Animal mob, IReadOnlyList<IMobTarget> targets, float deltaTime, ChunkManager chunks) =>
        mob.Heeds(targets) ? mob.PickTarget(targets, deltaTime, chunks) : -1;

    /// <inheritdoc />
    protected override Animal? Create(string kind, Vector3 position, bool spawnIn) =>
        WildlifeSpecies.KindOf(kind) is { } species && _types[(int)species] is { } type
            ? WildlifeSpecies.Create(type, position, spawnIn)
            : null;

    /// <summary>Ticks every animal against <paramref name="targets"/>, then the herd spawner.</summary>
    public void Update(float deltaTime, ChunkManager chunks, DropManager drops, IReadOnlyList<IMobTarget> targets,
        BlockDebrisSystem? debris = null)
    {
        if (chunks == null || targets == null) return;

        SendThieves(drops);
        UpdateMobs(deltaTime, chunks, drops, targets, debris, WildlifeConfig.DespawnRadiusBlocks);
        RallyFlocks(targets);
        TakeLoot(drops);
        UpdateSpawning(deltaTime, chunks, targets);
    }

    // Each calm crow with nothing in its claws picks the nearest loose item within its range; a prize another
    // crow took, or a player collected, is forgotten
    private void SendThieves(DropManager drops)
    {
        var loose = drops.Drops;
        if (loose.Count == 0) return;
        var rangeSquared = WildlifeConfig.CrowThiefRangeBlocks * WildlifeConfig.CrowThiefRangeBlocks;
        foreach (var mob in Mobs)
        {
            if (mob is not Crow crow) continue;
            if (crow.Prize is { } prize)
            {
                var stillThere = false;
                for (var i = 0; i < loose.Count && !stillThere; i++) stillThere = ReferenceEquals(loose[i], prize);
                if (stillThere && !crow.IsProvoked) continue;
                crow.SetPrize(null);
            }

            if (!crow.WantsPrize) continue;
            ItemDrop? nearest = null;
            var nearestSquared = rangeSquared;
            for (var i = 0; i < loose.Count; i++)
            {
                var drop = loose[i];
                if (drop.IsAttracted || !drop.IsGrounded) continue;
                var distance = Vector3.DistanceSquared(drop.Position, crow.Position);
                if (distance >= nearestSquared) continue;
                nearestSquared = distance;
                nearest = drop;
            }
            crow.SetPrize(nearest);
        }
    }

    // A crow that was hurt, or is already going after a player, calls its flock: every calm crow within earshot
    // (one that wandered into range later included) turns on the attacker as well
    private void RallyFlocks(IReadOnlyList<IMobTarget> targets)
    {
        var rangeSquared = WildlifeConfig.CrowFlockCallRangeBlocks * WildlifeConfig.CrowFlockCallRangeBlocks;
        foreach (var mob in Mobs)
        {
            if (mob is not Crow caller) continue;
            if (!caller.ConsumeRallyCall() && !caller.Heeds(targets)) continue;
            foreach (var other in Mobs)
            {
                if (other is Crow flockmate && !ReferenceEquals(flockmate, caller) &&
                    Vector3.DistanceSquared(flockmate.Position, caller.Position) <= rangeSquared)
                    flockmate.JoinFlock();
            }
        }
    }

    // A crow that reached its prize flies off with it
    private void TakeLoot(DropManager drops)
    {
        foreach (var mob in Mobs)
        {
            if (mob is not Crow { PrizeInReach: true } crow) continue;
            drops.Remove(crow.Prize!);
            crow.FinishErrand();
        }
    }

    // An attack (a gore, a swoop, a bite) lands on every non-immune target it still reaches at its clip's impact
    /// <inheritdoc />
    protected override void Attack(Animal mob, IReadOnlyList<IMobTarget> targets)
    {
        if (mob is CorpseBloom bloom) PoisonInCloud(bloom, targets);
        if (!mob.TryTakeStrike()) return;
        for (var t = 0; t < targets.Count; t++)
        {
            var target = targets[t];
            if (target.IsImmune || !mob.StrikeReaches(target.Eye)) continue;
            Hit(target, mob.StrikeDamage, mob.Position, mob.StrikeKnockbackSpeed, mob.StrikeKnockbackLift,
                mob.Type.DisplayName);
            if (mob.StrikeEffect is { } effect) StatusEffects.Apply(target, effect, 1f);
        }
    }

    // A corpse bloom's cloud lingers after its burst: each pulse poisons every target still standing in it
    private static void PoisonInCloud(CorpseBloom bloom, IReadOnlyList<IMobTarget> targets)
    {
        if (!bloom.TakeCloudPulse()) return;
        for (var t = 0; t < targets.Count; t++)
        {
            var target = targets[t];
            if (!target.IsImmune && bloom.CloudReaches(target.Eye)) StatusEffects.Apply(target, CorpseBloom.PoisonEffect, 1f);
        }
    }

    /// <inheritdoc />
    protected override float ShoveSpeedOf(Animal mob) => mob.ShoveSpeed;

    // Each turn of the shared timer searches the surface ring round the next target in turn (never one
    // underground, who would not see the herd), rolls the species its column holds and places that one animal.
    private void UpdateSpawning(float deltaTime, ChunkManager chunks, IReadOnlyList<IMobTarget> targets)
    {
        if (!TryTakeSpawnTurn(deltaTime, targets, WildlifeConfig.SpawnIntervalSeconds, out var target)) return;
        if (MobSpawner.TargetUnderground(chunks, target.Eye)) return;

        if (!MobSpawner.TryFindSurfaceSpawn(chunks, SpawnOrigin.Of(target), WildlifeConfig.SpawnMinRadiusBlocks,
                WildlifeConfig.SpawnMaxRadiusBlocks, WildlifeConfig.SpawnTries, out var x, out var groundY, out var z))
            return;

        var nearWater = MobSpawner.IsNearSurfaceWater(chunks, x, z, WildlifeConfig.NearWaterRadiusBlocks);
        var picked = WildlifeSpawnRules.Pick(MobSpawner.SurfaceBiomeAt(chunks, x, z), nearWater, Random.Shared.NextSingle(),
            kind => _types[(int)kind] != null);
        if (picked is not { } rule) return;

        Place(chunks, rule.Kind, x, groundY, z);
    }

    // One animal of the species over ground cell (x, y, z): standing on it, or for a flyer in the air a few
    // blocks above; false when its body would overlap a block there (a wide body beside a trunk, a flyer under
    // a canopy) or the hard cap is full
    private bool Place(ChunkManager chunks, WildlifeKind kind, int x, int y, int z)
    {
        var name = WildlifeSpecies.NameOf(kind);
        var height = WildlifeSpecies.HeightOf(name);
        var halfWidth = WildlifeSpecies.HalfExtentOf(name);
        var center = new Vector3(x + 0.5f, MobSpawner.StandingCenterY(y, height * 0.5f), z + 0.5f);
        if (WildlifeSpecies.Flies(kind))
            center.Y += MathUtils.Lerp(WildlifeConfig.AirSpawnLiftMinBlocks, WildlifeConfig.AirSpawnLiftMaxBlocks,
                Random.Shared.NextSingle());
        if (BoxSweep.IsColliding(center, new Vector3(halfWidth, height * 0.5f, halfWidth), chunks)) return false;
        return Spawn(name, center) != null;
    }
}
