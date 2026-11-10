using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.Entities;
using VoxelEngine.Entities.CaveMobs;
using VoxelEngine.Entities.Spiders;
using VoxelEngine.Particles;
using VoxelEngine.Player;
using VoxelEngine.Status;
using VoxelEngine.WorldGen.Hell;

namespace VoxelEngine.Simulation.Caves;

/// <summary>
/// Owns the cave sub-biomes' mobs (<see cref="Shardling"/>s, <see cref="Sporeling"/>s and <see cref="Magmaborn"/>s),
/// the <see cref="Spider"/>s that crawl through every cave, and the crystal shards the shardlings throw: on the shared
/// realm frame (<see cref="RealmMobManager{TMob}"/>) it launches a shard at each cast release and chills a target a
/// shard touches (damage plus <c>frostburn</c>), fills every target inside a sporeling's burst with spores (damage plus
/// <c>spore</c>), sets a target a magmaborn's blow lands on burning and poisons one a spider bites (<c>venom</c>). It
/// spawns new mobs on dark cave floors (<see cref="MobSpawner.TryFindCaveSpawn"/>) around a target standing
/// underground outside hell: the floor's cave biome and a roll pick the kind (<see cref="CaveSpawnRules.KindFor"/>),
/// its depth the tier (<see cref="CaveDifficulty.TierAt"/>), and the pack mates stay on the leader's biome and tier.
/// At night a spider now and then turns up on the surface too. The broodmother is never spawned here: it is the
/// spider den's miniboss. Every spawn is judged by the global <see cref="MobCaps"/> it shares with the other mob
/// kinds. Main thread only.
/// </summary>
public sealed class CaveMobManager : RealmMobManager<CaveMob>
{
    private const string ChillEffect = "frostburn";
    private const string SporeEffect = "spore";
    private const string BurningEffect = "burning";
    private const string VenomEffect = "venom";

    private readonly EntityType? _shardlingType;
    private readonly EntityType? _sporelingType;
    private readonly EntityType? _magmabornType;
    private readonly EntityType? _spiderType;
    private readonly EntityType? _broodmotherType;
    private float _surfaceTimer;
    private int _surfaceCursor;
    // Each shard carries its shardling's damage, scaled by the shardling's tier
    private readonly MobMissiles _shards = new();
    private readonly ushort _shardItemId;

    // The ring search around a target: dark, unlit cave floors whose cave biome holds a cave mob kind
    private static readonly CaveSpawnSearch RingSearch = new(
        MobConfig.CaveMobSpawnMinRadius, MobConfig.CaveMobSpawnMaxRadius, MobConfig.CaveMobSpawnTries,
        MobConfig.CaveMobSearchHalfHeight, MobConfig.CaveMobScanDepth, MobConfig.CaveMobMaxSkyLight,
        MobConfig.CaveMobSpawnBlockLightLimit, CaveSpawnRules.CaveMobMask);

    // A pack mate's search round the leader's floor; its biome mask is narrowed to the leader's biome per spawn
    private static readonly CaveSpawnSearch PackSearch = new(
        1f, MobConfig.CaveMobPackSpawnRadiusBlocks, MobConfig.CaveMobSpawnTries,
        MobConfig.CaveMobPackSearchHalfHeight, MobConfig.CaveMobPackScanDepth, MobConfig.CaveMobMaxSkyLight,
        MobConfig.CaveMobSpawnBlockLightLimit, CaveSpawnRules.CaveMobMask);

    /// <summary>
    /// Validates every kind's content first, so a model missing a part, clip or event fails at startup naming its
    /// file. Any type may be null (unregistered content), which leaves that kind out. Joins <paramref name="caps"/>
    /// (the world's), or caps itself alone when none is given (tests).
    /// </summary>
    public CaveMobManager(EntityType? shardlingType, EntityType? sporelingType, EntityType? magmabornType,
        MobCaps? caps = null, EntityType? spiderType = null, EntityType? broodmotherType = null) : base(caps)
    {
        if (shardlingType != null) Shardling.ValidateContent(shardlingType);
        if (sporelingType != null) Sporeling.ValidateContent(sporelingType);
        if (magmabornType != null) Magmaborn.ValidateContent(magmabornType);
        if (spiderType != null) Spider.ValidateContent(spiderType);
        if (broodmotherType != null) Spider.ValidateContent(broodmotherType);
        _shardlingType = shardlingType;
        _sporelingType = sporelingType;
        _magmabornType = magmabornType;
        _spiderType = spiderType;
        _broodmotherType = broodmotherType;
        _shardItemId = ContentRegistry.RequireBlock("crystal_block").Id;
    }

    /// <summary>Crystal shards in flight; drawn and mirrored like the player's own throws.</summary>
    public IReadOnlyList<ThrownProjectile> Shards => _shards.Flying;

    /// <summary>Test seam: the cave biome of a spawn floor's standing cell; null reads the world's own
    /// (<see cref="MobSpawner.TryFindCaveSpawn"/>'s default).</summary>
    internal CaveBiomeResolver? Biomes { get; set; }

    /// <summary>The body height of the named kind (where a scenario or a ring spawn puts its center above the
    /// feet), or 0 for an unknown kind.</summary>
    public static float HeightOf(string kind) =>
        string.Equals(kind, Shardling.EntityName, StringComparison.OrdinalIgnoreCase) ? MobConfig.CaveShardlingHeight
        : string.Equals(kind, Sporeling.EntityName, StringComparison.OrdinalIgnoreCase) ? MobConfig.CaveSporelingHeight
        : string.Equals(kind, Magmaborn.EntityName, StringComparison.OrdinalIgnoreCase) ? MobConfig.CaveMagmabornHeight
        : Spider.BodySizeOf(kind);

    /// <summary>
    /// Adds a mob of the named kind at an exact body-center position with an explicit depth
    /// <paramref name="tier"/> (clamped): a forced spawn (save restores, a client's mirror, the ring spawner, a
    /// scenario's tier), which may pass the natural cap but never the hard cap. Null for an unknown or unregistered
    /// kind or a full hard cap.
    /// </summary>
    public CaveMob? Spawn(string kind, Vector3 position, int tier, bool spawnIn = true)
    {
        if (Caps.ForcedRoom <= 0) return null;
        var mob = Build(kind, position, tier, spawnIn);
        return mob == null ? null : Track(mob);
    }

    /// <inheritdoc />
    /// <remarks>The tier comes from the spawn point's depth (<see cref="CaveDifficulty.TierAt"/>).</remarks>
    protected override CaveMob? Create(string kind, Vector3 position, bool spawnIn) =>
        Build(kind, position, CaveDifficulty.TierAt(position.Y), spawnIn);

    private CaveMob? Build(string kind, Vector3 position, int tier, bool spawnIn)
    {
        if (_shardlingType != null && string.Equals(kind, Shardling.EntityName, StringComparison.OrdinalIgnoreCase))
            return new Shardling(_shardlingType, position, tier, spawnIn);
        if (_sporelingType != null && string.Equals(kind, Sporeling.EntityName, StringComparison.OrdinalIgnoreCase))
            return new Sporeling(_sporelingType, position, tier, spawnIn);
        if (_magmabornType != null && string.Equals(kind, Magmaborn.EntityName, StringComparison.OrdinalIgnoreCase))
            return new Magmaborn(_magmabornType, position, tier, spawnIn);
        if (_spiderType != null && string.Equals(kind, Spider.EntityName, StringComparison.OrdinalIgnoreCase))
            return new Spider(_spiderType, position, tier, broodmother: false, spawnIn);
        if (_broodmotherType != null && string.Equals(kind, Spider.BroodmotherEntityName, StringComparison.OrdinalIgnoreCase))
            return new Spider(_broodmotherType, position, tier, broodmother: true, spawnIn);
        return null;
    }

    /// <summary>Test seam: launches a shard as if a shardling had thrown it, carrying <paramref name="damage"/>.</summary>
    internal ThrownProjectile TestLaunchShard(Vector3 origin, Vector3 velocity, int damage) =>
        _shards.Launch(_shardItemId, origin, velocity, damage);

    /// <summary>Ticks every cave mob and shard against <paramref name="targets"/> (each mob chasing its noticed
    /// target, <see cref="Mob.PickTarget"/>), then the ring spawners; <paramref name="night"/> (0..1, the lighting
    /// clock's) opens the surface to the odd spider.</summary>
    public void Update(float deltaTime, ChunkManager chunks, DropManager drops, IReadOnlyList<IMobTarget> targets,
        BlockDebrisSystem? debris = null, float night = 0f)
    {
        if (chunks == null || targets == null) return;

        UpdateMobs(deltaTime, chunks, drops, targets, debris, MobConfig.CaveMobDespawnRadiusBlocks);
        UpdateShards(deltaTime, chunks, targets);
        UpdateSpawning(deltaTime, chunks, targets);
        UpdateSurfaceSpawning(deltaTime, chunks, targets, night);
    }

    // A shardling's release launches its shard; a sporeling's burst lands on every non-immune target inside it at
    // the clip's impact, a magmaborn's blow on every non-immune target still in reach
    /// <inheritdoc />
    protected override void Attack(CaveMob mob, IReadOnlyList<IMobTarget> targets)
    {
        switch (mob)
        {
            case Shardling shardling:
                if (shardling.TryTakeShotRelease(out var origin, out var velocity))
                    _shards.Launch(_shardItemId, origin, velocity, shardling.ShardDamage);
                return;

            case Sporeling sporeling:
                if (!sporeling.TryTakeBurstImpact()) return;
                for (var t = 0; t < targets.Count; t++)
                {
                    var target = targets[t];
                    if (target.IsImmune || !Sporeling.InBurst(sporeling.Feet, target.Eye)) continue;
                    Hit(target, sporeling.BurstDamage, sporeling.Position, MobConfig.CaveSporeKnockbackSpeed,
                        MobConfig.CaveSporeKnockbackLift, sporeling.Type.DisplayName);
                    StatusEffects.Apply(target, SporeEffect, MobConfig.CaveSporeScale);
                }
                return;

            case Magmaborn brute:
                if (!brute.TryTakeMeleeImpact()) return;
                for (var t = 0; t < targets.Count; t++)
                {
                    var target = targets[t];
                    if (target.IsImmune || !Magmaborn.InMeleeReach(brute.Position, target.Eye)) continue;
                    Hit(target, brute.MeleeDamage, brute.Position, MobConfig.CaveMagmabornKnockbackSpeed,
                        MobConfig.CaveMagmabornKnockbackLift, brute.Type.DisplayName);
                    StatusEffects.Apply(target, BurningEffect, MobConfig.CaveMagmabornBurnScale);
                }
                return;

            case Spider spider:
                if (!spider.TryTakeBiteImpact()) return;
                for (var t = 0; t < targets.Count; t++)
                {
                    var target = targets[t];
                    if (target.IsImmune || !spider.InBiteReach(spider.Position, target.Eye)) continue;
                    Hit(target, spider.BiteDamage, spider.Position, spider.KnockbackSpeed, spider.KnockbackLift,
                        spider.Type.DisplayName);
                    StatusEffects.Apply(target, VenomEffect, spider.VenomScale);
                }
                return;
        }
    }

    /// <inheritdoc />
    protected override float ShoveSpeedOf(CaveMob mob) => mob switch
    {
        Magmaborn => MobConfig.CaveMagmabornShoveSpeed,
        Sporeling => MobConfig.CaveSporelingShoveSpeed,
        Spider spider => spider.ShoveSpeed,
        _ => MobConfig.CaveShardlingShoveSpeed,
    };

    // A shard flies under gravity; any target it touches stops it, and the first non-immune one takes its damage
    // and is chilled. A shard that strikes a block or outlives its flight shatters.
    private void UpdateShards(float deltaTime, ChunkManager chunks, IReadOnlyList<IMobTarget> targets)
    {
        _shards.Update(deltaTime, chunks, targets);

        var hits = _shards.Hits;
        for (var i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            Hit(hit.Target, hit.Damage, hit.Position, MobConfig.CaveShardKnockbackSpeed, 0f,
                _shardlingType?.DisplayName);
            StatusEffects.Apply(hit.Target, ChillEffect, MobConfig.CaveShardChillScale);
        }
    }

    // Each turn of the shared timer tries the ring round the next target in turn, and only a target standing
    // underground outside hell is searched around; the floor's cave biome picks the kind and its depth the tier.
    private void UpdateSpawning(float deltaTime, ChunkManager chunks, IReadOnlyList<IMobTarget> targets)
    {
        if (_shardlingType == null && _sporelingType == null && _magmabornType == null && _spiderType == null) return;
        if (!TryTakeSpawnTurn(deltaTime, targets, MobConfig.CaveMobSpawnIntervalSeconds, out var target)) return;

        var eye = target.Eye;
        if (HellGenerator.InHell(MathUtils.FloorToInt(eye.Y - Camera.EyeHeight))) return;
        if (!MobSpawner.TargetUnderground(chunks, eye)) return;

        // Spiders take any cave, so with them the ring accepts every biome's floor
        var spiders = _spiderType != null;
        var search = spiders ? RingSearch with { AllowedBiomes = CaveSpawnRules.AllBiomes } : RingSearch;
        if (!MobSpawner.TryFindCaveSpawn(chunks, SpawnOrigin.Of(target), search, Biomes, out var x, out var groundY, out var z,
                out var biome))
            return;

        var kind = CaveSpawnRules.KindFor(biome, Random.Shared.NextSingle(), spiders);
        if (kind == null) return;

        var tier = CaveDifficulty.TierAt(groundY + 1);
        if (SpawnOnFloor(kind, x, groundY, z, tier) == null) return;

        var packMax = kind == Spider.EntityName ? SpiderConfig.CavePackMaxSize : MobConfig.CaveMobPackMaxSize;
        var pack = 1 + Random.Shared.Next(packMax);
        var leaderEye = new Vector3(x + 0.5f, groundY + 1 + Camera.EyeHeight, z + 0.5f);
        var packSearch = PackSearch with { AllowedBiomes = CaveSpawnRules.Bit(biome) };
        for (var member = 1; member < pack && Caps.AllowsNaturalSpawn; member++)
        {
            if (MobSpawner.TryFindCaveSpawn(chunks, leaderEye, packSearch, Biomes, out var mx, out var my, out var mz,
                    out _))
                SpawnOnFloor(kind, mx, my, mz, tier);
        }
    }

    // The odd spider on the surface: at night, on its own slow timer under the shared natural cap, one try on the
    // dry surface ring of the next target in turn that is not underground
    private void UpdateSurfaceSpawning(float deltaTime, ChunkManager chunks, IReadOnlyList<IMobTarget> targets, float night)
    {
        if (_spiderType == null || targets.Count == 0 || !Caps.AllowsNaturalSpawn) return;
        if (night < 0.5f) return;

        _surfaceTimer += deltaTime * Caps.SpawnHaste;
        if (_surfaceTimer < SpiderConfig.SurfaceSpawnIntervalSeconds) return;
        _surfaceTimer = 0f;
        if (Random.Shared.NextSingle() >= SpiderConfig.SurfaceSpawnChance) return;

        var index = _surfaceCursor % targets.Count;
        _surfaceCursor = index + 1;
        var target = targets[index];
        if (HellGenerator.InHell(MathUtils.FloorToInt(target.Eye.Y - Camera.EyeHeight))) return;
        if (MobSpawner.TargetUnderground(chunks, target.Eye)) return;
        if (!MobSpawner.TryFindSurfaceSpawn(chunks, SpawnOrigin.Of(target), SpiderConfig.SurfaceSpawnMinRadius,
                SpiderConfig.SurfaceSpawnMaxRadius, SpiderConfig.SurfaceSpawnTries, out var x, out var groundY, out var z))
            return;

        SpawnOnFloor(Spider.EntityName, x, groundY, z, tier: 0);
    }

    // One mob of the kind standing on floor cell (x, y, z) at the given tier; null when the kind is not registered or
    // the hard cap is full
    private CaveMob? SpawnOnFloor(string kind, int x, int y, int z, int tier) =>
        Spawn(kind, new Vector3(x + 0.5f, y + 1 + HeightOf(kind) * 0.5f, z + 0.5f), tier);
}
