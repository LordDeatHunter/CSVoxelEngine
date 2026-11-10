using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Entities;
using VoxelEngine.Entities.SkyMobs;
using VoxelEngine.Particles;
using VoxelEngine.Player;

namespace VoxelEngine.Simulation;

/// <summary>
/// Owns the sky islands' mobs (<see cref="Galeraptor"/>s and <see cref="Cloudgolem"/>s): on the shared realm
/// frame (<see cref="RealmMobManager{TMob}"/>) it bills a galeraptor's strike at its dive impact to every target
/// still in reach and a golem's shockwave at its slam impact to every target inside it, and spawns new mobs
/// only on island tops (<see cref="MobSpawner.TryFindSkySpawn"/>) around a target standing in the sky band,
/// scaled by the tier of the island zone they land in (<see cref="SkyDifficulty"/>: health, damage, pack size,
/// and the golem only in the big zones); a galeraptor appears in the air over the island top. Every spawn is
/// judged by the global <see cref="MobCaps"/> it shares with the other mob kinds. Main thread only.
/// </summary>
public sealed class SkyMobManager : RealmMobManager<SkyMob>
{
    private readonly EntityType? _flyerType;
    private readonly EntityType? _bruteType;

    /// <summary>
    /// Validates both kinds' content first, so a model missing a part, clip or event fails at startup
    /// naming its file. Either type may be null (unregistered content), which leaves that kind out.
    /// Joins <paramref name="caps"/> (the world's), or caps itself alone when none is given (tests).
    /// </summary>
    public SkyMobManager(EntityType? flyerType, EntityType? bruteType, MobCaps? caps = null) : base(caps)
    {
        if (flyerType != null) Galeraptor.ValidateContent(flyerType);
        if (bruteType != null) Cloudgolem.ValidateContent(bruteType);
        _flyerType = flyerType;
        _bruteType = bruteType;
    }

    /// <summary>The body height of the named kind (where a scenario or a ring spawn puts its center above
    /// the feet), or 0 for an unknown kind.</summary>
    public static float HeightOf(string kind) =>
        string.Equals(kind, Galeraptor.EntityName, StringComparison.OrdinalIgnoreCase) ? MobConfig.GaleraptorHeight
        : string.Equals(kind, Cloudgolem.EntityName, StringComparison.OrdinalIgnoreCase) ? MobConfig.CloudgolemHeight
        : 0f;

    /// <inheritdoc />
    protected override SkyMob? Create(string kind, Vector3 position, bool spawnIn)
    {
        if (_flyerType != null && string.Equals(kind, Galeraptor.EntityName, StringComparison.OrdinalIgnoreCase))
            return new Galeraptor(_flyerType, position, spawnIn);
        if (_bruteType != null && string.Equals(kind, Cloudgolem.EntityName, StringComparison.OrdinalIgnoreCase))
            return new Cloudgolem(_bruteType, position, spawnIn);
        return null;
    }

    /// <summary>Ticks every sky mob against <paramref name="targets"/> (each chasing its noticed target,
    /// <see cref="Mob.PickTarget"/>), then the ring spawner.</summary>
    public void Update(float deltaTime, ChunkManager chunks, DropManager drops, IReadOnlyList<IMobTarget> targets,
        BlockDebrisSystem? debris = null)
    {
        if (chunks == null || targets == null) return;

        UpdateMobs(deltaTime, chunks, drops, targets, debris, MobConfig.SkyDespawnRadiusBlocks);
        UpdateSpawning(deltaTime, chunks, targets);
    }

    // A galeraptor's strike lands on every non-immune target whose chest is in reach at the dive's impact; a
    // golem's shockwave on every non-immune target inside it at the slam's
    /// <inheritdoc />
    protected override void Attack(SkyMob mob, IReadOnlyList<IMobTarget> targets)
    {
        if (mob is Galeraptor raptor)
        {
            if (raptor.Carried is { } held)
            {
                // Hoist the held target skyward each frame; once high enough (or too long) the talons open
                if (raptor.CarryDone(FrameDelta)) raptor.Release();
                else held.TakeHit(0, Vector3.Zero, 0f, MobConfig.GaleraptorCarryLiftSpeed, raptor.Type.DisplayName);
                return;
            }

            if (!raptor.TryTakeDiveImpact()) return;
            for (var t = 0; t < targets.Count; t++)
            {
                var target = targets[t];
                if (target.IsImmune || !Galeraptor.InDiveReach(raptor.Position, target.Eye)) continue;
                Hit(target, raptor.DiveDamage, raptor.Position, MobConfig.GaleraptorKnockbackSpeed,
                    MobConfig.GaleraptorKnockbackLift, raptor.Type.DisplayName);
                raptor.Grab(target);
                break;
            }
            return;
        }

        if (mob is not Cloudgolem golem || !golem.TryTakeSlamImpact()) return;
        for (var t = 0; t < targets.Count; t++)
        {
            var target = targets[t];
            if (target.IsImmune || !Cloudgolem.InShockwave(golem.Feet, target.Eye)) continue;
            Hit(target, golem.SlamDamage, golem.Position, MobConfig.CloudgolemKnockbackSpeed,
                MobConfig.CloudgolemKnockbackLift, golem.Type.DisplayName);
        }
    }

    /// <inheritdoc />
    protected override float ShoveSpeedOf(SkyMob mob) =>
        mob is Cloudgolem ? MobConfig.CloudgolemShoveSpeed : MobConfig.GaleraptorShoveSpeed;

    // Each turn of the shared timer tries the ring round the next target in turn, and only a target standing
    // in the sky band is searched around.
    private void UpdateSpawning(float deltaTime, ChunkManager chunks, IReadOnlyList<IMobTarget> targets)
    {
        if (_flyerType == null && _bruteType == null) return;
        if (!TryTakeSpawnTurn(deltaTime, targets, MobConfig.SkySpawnIntervalSeconds, out var target)) return;

        var eye = target.Eye;
        if (!MobSpawner.InSkyBand(MathUtils.FloorToInt(eye.Y - Camera.EyeHeight))) return;

        if (!MobSpawner.TryFindSkySpawn(chunks, SpawnOrigin.Of(target), MobConfig.SkySpawnMinRadiusBlocks,
                MobConfig.SkySpawnMaxRadiusBlocks, MobConfig.SkySpawnTries, MobConfig.SkySpawnSearchHalfHeight,
                MobConfig.SkySpawnScanDepth, out var x, out var groundY, out var z))
            return;

        var tier = SkyDifficulty.TierAt(x + 0.5f, z + 0.5f);
        var pack = 1 + Random.Shared.Next(SkyDifficulty.PackSize(tier));
        SpawnOnIsland(chunks, x, groundY, z, tier);
        for (var member = 1; member < pack && Caps.AllowsNaturalSpawn; member++)
        {
            if (MobSpawner.TryFindSkySpawn(chunks, new Vector3(x + 0.5f, groundY + 1 + Camera.EyeHeight, z + 0.5f),
                    1f, MobConfig.SkyPackSpawnRadiusBlocks, MobConfig.SkySpawnTries, 2, 6,
                    out var mx, out var my, out var mz))
                SpawnOnIsland(chunks, mx, my, mz, tier);
        }
    }

    // One ring spawn on island top (x, y, z) of a zone of the given tier: a golem standing on it (big zones
    // only), else a galeraptor in the clear air SkyFlyerSpawnLiftMin..Max blocks over it; nothing when the
    // rolled kind is not registered or the air over the top is not open.
    private void SpawnOnIsland(ChunkManager chunks, int x, int y, int z, int tier)
    {
        var kind = SkyDifficulty.KindFor(tier, Random.Shared.NextSingle());
        if (kind == Cloudgolem.EntityName)
        {
            if (_bruteType != null)
                Spawn(kind, new Vector3(x + 0.5f, y + 1 + MobConfig.CloudgolemHeight * 0.5f, z + 0.5f));
            return;
        }

        if (_flyerType == null) return;
        var lift = MathUtils.Lerp(MobConfig.SkyFlyerSpawnLiftMin, MobConfig.SkyFlyerSpawnLiftMax, Random.Shared.NextSingle());
        var center = new Vector3(x + 0.5f, y + 1 + lift, z + 0.5f);
        if (OpenAirAbove(chunks, x, y, z, center.Y + MobConfig.GaleraptorHeight * 0.5f))
            Spawn(kind, center);
    }

    // Whether every cell from the island top up to the height top is loaded and not solid, so a flyer
    // appearing there neither starts inside a block nor over a ceiling it cannot leave
    private static bool OpenAirAbove(ChunkManager chunks, int x, int floorY, int z, float top)
    {
        var last = MathUtils.FloorToInt(top);
        for (var y = floorY + 1; y <= last; y++)
            if (!chunks.TryGetCell(x, y, z, out var id, out _) || BlockType.IsSolid(id))
                return false;
        return true;
    }
}
