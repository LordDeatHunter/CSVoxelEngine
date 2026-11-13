using System;
using System.Numerics;
using VoxelEngine.Audio;
using VoxelEngine.Entities;
using VoxelEngine.Items;
using VoxelEngine.Particles;
using VoxelEngine.Player;
using VoxelEngine.Rendering.Overlays;
using VoxelEngine.Status;

namespace VoxelEngine.Interaction;

/// <summary>
/// What a landed hit does to a mob: its damage and shove, the pale hit sparks off its body and the
/// floating damage number and sound over it. Every strike path (punch, slice, thrown items) and the client's
/// snapshot health drops go through it. Owned by <see cref="World"/>, whose
/// <see cref="World.DamageNumbers"/> pool it fills; main thread only.
/// </summary>
internal sealed class MobStrikes
{
    private readonly World _world;
    private readonly BlockDebrisSystem _debris;
    private readonly WorldSounds _sounds;
    private readonly bool _serverMode;

    private DamageNumberPool DamageNumbers => _world.DamageNumbers;

    internal MobStrikes(World world, BlockDebrisSystem debris, WorldSounds sounds, bool serverMode)
    {
        _world = world;
        _debris = debris;
        _sounds = sounds;
        _serverMode = serverMode;
    }

    /// <summary>Deals <paramref name="damage"/> to <paramref name="mob"/> and shoves it away from
    /// <paramref name="from"/> (the striker's eye, or where a thrown item left it) at
    /// <paramref name="shoveSpeed"/> (a punch's by default; a sword's chop throws harder). A landed blow
    /// from a player (<paramref name="striker"/>) puts that player in combat, which pauses their regeneration, and
    /// that player's worn undead bonus raises a blow against an undead mob. A landed blow also sprays
    /// <paramref name="shrapnel"/>, the payload the weapon path resolved from the held item and the striker's
    /// gear; it stays empty for a thrown item, a spell and a fragment's own hit, so shrapnel never cascades.
    /// The blade it was struck with (<paramref name="weapon"/>) puts its <c>onHit</c> effect on the mob.
    /// Returns whether the mob's pool accepted the blow.</summary>
    internal bool StrikeMob(Mob mob, int damage, Vector3 from, PlayerHealth? striker = null,
        float shoveSpeed = MobConfig.SlimePunchShoveSpeed, in ShrapnelProfile shrapnel = default, Item? weapon = null)
    {
        var hits = mob.HitsTaken;
        var wasAlive = !mob.IsDead;
        damage = Combat.VersusUndead(damage, mob.IsUndead, striker?.Gear?.Stats.UndeadDamage ?? 0f);
        mob.TakeDamage(mob.ScaleDamage(damage, from));
        var landed = mob.HitsTaken != hits;
        if (landed)
        {
            // The killing blow credits the striker's quests
            if (wasAlive && mob.IsDead) _world.QuestsOf(striker)?.OnKilled(mob.Type.Name);
            striker?.NoteCombat();
            // Worn gear's on-hit passives (a client never strikes a mob: its mobs are mirrors)
            GearPassives.OnHit(striker, mob, Random.Shared.NextSingle);
            // The blade's own on-hit effect (a cinder sword sets its target alight)
            if (weapon?.OnHit is { } onHit && Random.Shared.NextSingle() < onHit.Chance)
                StatusEffects.Apply(mob, onHit.Effect, onHit.Scale);
            if (!shrapnel.IsEmpty) _world.Shrapnel.Burst(mob, shrapnel, striker);
        }
        ShowLandedHit(mob, hits);
        SpawnHitSparks(mob, from);
        // A mob mid-attack is not knocked back, so a hit never cancels its swing or throw
        if (mob.IsMidAttack) return landed;
        // A flyer is knocked away in 3D (up off a blow from below), a ground mob sideways
        var away = mob.Position - from;
        if (!mob.Flies) away.Y = 0f;
        mob.Shove(away, shoveSpeed);
        return landed;
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
        SpawnGore(mob, contact, -direction);
    }

    // The mob's blood (its entity file's `blood`) spurts out the far side of the blow; a kill sprays more
    private void SpawnGore(Mob mob, Vector3 contact, Vector3 away)
    {
        if (mob.Type.BloodColor is not { } color) return;
        var count = mob.IsDead ? JuiceConfig.GoreKillCount : JuiceConfig.GoreHitCount;
        _debris.Gore(contact + new Vector3(0f, mob.BodyHalfHeight * 0.2f, 0f), away, color, count,
            JuiceConfig.GoreSpeed, JuiceConfig.GoreSize * mob.Type.BloodSizeScale);
    }

    // Plays the mob's hit (or, when it killed, death) sound at the mob and floats the damage of the hit over it
    // when TakeDamage accepted the hit (its hit count moved). A headless server draws nothing, but sends the
    // sound to the players near the mob (a client's mirrors are never struck, so it hears only the server's).
    internal void ShowLandedHit(Mob mob, int hitsBefore)
    {
        if (mob.HitsTaken == hitsBefore) return;
        var sound = mob.IsDead ? mob.DeathSoundGroup : mob.HitSoundGroup;
        if (sound != null) _sounds.PlayAt(sound, mob.Position);
        if (!_serverMode) ShowDamageNumber(mob, mob.LastHitDamage);
    }

    internal void ShowDamageNumber(Mob mob, int amount) =>
        DamageNumbers.Spawn(mob.Position with { Y = EntityHealthbarRenderer.BarCenterY(mob.Position.Y, mob.HeadAboveCenter)
                                                    + MobConfig.DamageNumberLiftBlocks }, amount);
}
