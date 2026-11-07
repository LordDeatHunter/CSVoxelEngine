using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Particles;
using VoxelEngine.Player;
using VoxelEngine.Rendering.Overlays;

namespace VoxelEngine.Interaction;

/// <summary>
/// What a landed hit does to a mob: its damage and shove, the pale hit sparks off its body and the
/// floating damage number over it. Every strike path (punch, slice, thrown items) and the client's
/// snapshot health drops go through it. Owned by <see cref="World"/>, whose
/// <see cref="World.DamageNumbers"/> pool it fills; main thread only.
/// </summary>
internal sealed class MobStrikes
{
    private readonly World _world;
    private readonly BlockDebrisSystem _debris;
    private readonly bool _serverMode;

    private DamageNumberPool DamageNumbers => _world.DamageNumbers;

    internal MobStrikes(World world, BlockDebrisSystem debris, bool serverMode)
    {
        _world = world;
        _debris = debris;
        _serverMode = serverMode;
    }

    /// <summary>Deals <paramref name="damage"/> to <paramref name="mob"/> and shoves it away from
    /// <paramref name="from"/> (the striker's eye, or where a thrown item left it) at
    /// <paramref name="shoveSpeed"/> (a punch's by default; a sword's chop throws harder). A landed blow
    /// from a player (<paramref name="striker"/>) puts that player in combat, which pauses their regeneration.</summary>
    internal void StrikeMob(Mob mob, int damage, Vector3 from, PlayerHealth? striker = null,
        float shoveSpeed = MobConfig.SlimePunchShoveSpeed)
    {
        var hits = mob.HitsTaken;
        mob.TakeDamage(damage);
        if (mob.HitsTaken != hits)
            striker?.NoteCombat();
        ShowLandedHit(mob, hits);
        SpawnHitSparks(mob, from);
        // A flyer is knocked away in 3D (up off a blow from below), a ground mob sideways
        var away = mob.Position - from;
        if (!mob.Flies) away.Y = 0f;
        mob.Shove(away, shoveSpeed);
    }

    // A connecting hit throws a few pale chips off the mob's body where the striker faces it
    // (cosmetic: a server draws nothing, and a client sparks its own predicted connect instead)
    internal void SpawnHitSparks(Mob mob, Vector3 from)
    {
        if (_serverMode) return;
        var toward = from - mob.Position;
        toward.Y = 0f;
        var direction = toward.LengthSquared() > 1e-6f ? Vector3.Normalize(toward) : Vector3.UnitZ;
        var contact = mob.Position + direction * (mob.BodyHalfExtent * 0.6f);
        _debris.Spark(contact, JuiceConfig.HitSparkColor, mob.BodyHalfExtent * 0.5f,
            JuiceConfig.HitSparkCount, JuiceConfig.HitSparkSpeed,
            JuiceConfig.HitSparkSize);
    }

    // Floats the damage of a hit over the mob when TakeDamage accepted it (its hit count moved). A
    // headless server draws nothing, so it shows none.
    internal void ShowLandedHit(Mob mob, int hitsBefore)
    {
        if (_serverMode || mob.HitsTaken == hitsBefore) return;
        ShowDamageNumber(mob, mob.LastHitDamage);
    }

    internal void ShowDamageNumber(Mob mob, int amount) =>
        DamageNumbers.Spawn(mob.Position with { Y = EntityHealthbarRenderer.BarCenterY(mob.Position.Y, mob.HeadAboveCenter)
                                                    + MobConfig.DamageNumberLiftBlocks }, amount);
}
