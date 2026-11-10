using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Wildlife;
using VoxelEngine.Interaction;
using VoxelEngine.Particles;

namespace VoxelEngine.Simulation;

/// <summary>
/// Owns the animals of the wild (<see cref="Animal"/>: <see cref="Deer"/>, <see cref="Boar"/>): on the shared
/// realm frame (<see cref="RealmMobManager{TMob}"/>) each animal reacts only to the players it heeds
/// (<see cref="Animal.Heeds"/>) and wanders otherwise, a boar's gore is billed at its impact to every target
/// still in reach, and nothing despawns for standing idle. It spawns herds on dry surface around the players
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

        UpdateMobs(deltaTime, chunks, drops, targets, debris, WildlifeConfig.DespawnRadiusBlocks);
        UpdateSpawning(deltaTime, chunks, targets);
    }

    // A boar's gore lands on every non-immune target still in reach at the clip's impact
    /// <inheritdoc />
    protected override void Attack(Animal mob, IReadOnlyList<IMobTarget> targets)
    {
        if (mob is not Boar boar || !boar.TryTakeGoreImpact()) return;
        for (var t = 0; t < targets.Count; t++)
        {
            var target = targets[t];
            if (target.IsImmune || !Boar.InGoreReach(boar.Position, target.Eye, boar.GoreReach)) continue;
            Hit(target, boar.GoreDamage, boar.Position,
                boar.IsDire ? WildlifeConfig.DireBoarKnockbackSpeed : WildlifeConfig.BoarKnockbackSpeed,
                boar.IsDire ? WildlifeConfig.DireBoarKnockbackLift : WildlifeConfig.BoarKnockbackLift,
                boar.Type.DisplayName);
        }
    }

    /// <inheritdoc />
    protected override float ShoveSpeedOf(Animal mob) => mob.ShoveSpeed;

    // Each turn of the shared timer searches the surface ring round the next target in turn (never one
    // underground, who would not see the herd), rolls the species its column holds and places its group.
    private void UpdateSpawning(float deltaTime, ChunkManager chunks, IReadOnlyList<IMobTarget> targets)
    {
        if (!TryTakeSpawnTurn(deltaTime, targets, WildlifeConfig.SpawnIntervalSeconds, out var target)) return;
        if (MobSpawner.TargetUnderground(chunks, target.Eye)) return;

        if (!MobSpawner.TryFindSurfaceSpawn(chunks, SpawnOrigin.Of(target), WildlifeConfig.SpawnMinRadiusBlocks,
                WildlifeConfig.SpawnMaxRadiusBlocks, WildlifeConfig.SpawnTries, out var x, out var groundY, out var z))
            return;

        var picked = WildlifeSpawnRules.Pick(MobSpawner.SurfaceBiomeAt(chunks, x, z), Random.Shared.NextSingle(),
            kind => _types[(int)kind] != null);
        if (picked is not { } rule) return;

        var name = WildlifeSpecies.NameOf(rule.Kind);
        if (!SpawnStanding(chunks, name, x, groundY, z)) return;

        var group = Random.Shared.Next(rule.GroupMin, rule.GroupMax + 1);
        var leader = new Vector3(x + 0.5f, groundY + 1.5f, z + 0.5f);
        for (var member = 1; member < group && Caps.AllowsWildlifeSpawn; member++)
        {
            if (MobSpawner.TryFindSurfaceSpawn(chunks, leader, 1f, WildlifeConfig.GroupSpawnRadiusBlocks,
                    WildlifeConfig.GroupSpawnTries, out var mx, out var my, out var mz))
                SpawnStanding(chunks, name, mx, my, mz);
        }
    }

    // One animal of the named species standing on ground cell (x, y, z); false when its body would overlap a
    // block there (a wide body beside a trunk) or the hard cap is full
    private bool SpawnStanding(ChunkManager chunks, string name, int x, int y, int z)
    {
        var height = WildlifeSpecies.HeightOf(name);
        var halfWidth = WildlifeSpecies.HalfExtentOf(name);
        var center = new Vector3(x + 0.5f, MobSpawner.StandingCenterY(y, height * 0.5f), z + 0.5f);
        if (BoxSweep.IsColliding(center, new Vector3(halfWidth, height * 0.5f, halfWidth), chunks)) return false;
        return Spawn(name, center) != null;
    }
}
