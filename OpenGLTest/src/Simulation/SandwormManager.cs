using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Loot;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Sandworms;
using VoxelEngine.Particles;
using VoxelEngine.Player;

namespace VoxelEngine.Simulation;

/// <summary>
/// Owns the sandworm boss (plans/SANDWORM.md): at most one living at a time, never spawned by a ring (it is
/// summoned, or placed by a scenario or a save restore). Each frame it picks the nearest desert target within
/// reach (a non-immune one first), lets the worm hunt it over the sand under the chain, bills the head's chomp
/// and the dash's contact to every target they touch, rolls the loot on a kill and retires a worm no target
/// is near. Joins the world's <see cref="MobCaps"/>. Main thread only.
/// </summary>
public sealed class SandwormManager : IMobPopulation
{
    private readonly EntityType? _type;
    private readonly MobCaps _caps;
    private readonly List<Sandworm> _worms = [];
    private readonly List<byte> _loot = [];

    /// <summary>Joins <paramref name="caps"/> (the world's), or caps itself alone when none is given (tests).
    /// <paramref name="type"/> may be null (unregistered content), which leaves the boss out.</summary>
    public SandwormManager(EntityType? type, MobCaps? caps = null)
    {
        _type = type;
        _caps = caps ?? new MobCaps();
        _caps.Register(this);
    }

    /// <summary>Every worm still drawn: the living one plus any playing its death.</summary>
    public IReadOnlyList<Sandworm> Worms => _worms;

    /// <summary>Worms that are alive (not yet dying), what the global caps count.</summary>
    public int LivingCount
    {
        get
        {
            var count = 0;
            for (var i = 0; i < _worms.Count; i++)
                if (!_worms[i].IsDying) count++;
            return count;
        }
    }

    /// <summary>Health the worm took off every target together during the most recent update; each hit also
    /// reached its target through <see cref="IMobTarget.TakeHit"/>.</summary>
    public int DamageDealtThisFrame { get; private set; }

    /// <summary>
    /// Puts a worm with its head at <paramref name="head"/> (under the sand for a summon), unless one is alive
    /// already, the content is unregistered or the hard mob cap is full: a forced spawn that may pass the natural
    /// cap. Null when refused.
    /// </summary>
    public Sandworm? Spawn(Vector3 head, float heading = 0f)
    {
        if (_type == null || LivingCount > 0 || _caps.ForcedRoom <= 0) return null;
        var worm = new Sandworm(_type, head, heading);
        _worms.Add(worm);
        return worm;
    }

    /// <summary>A client mirror of the server worm first seen with its head at <paramref name="head"/> facing
    /// <paramref name="facing"/> (radians): display-only, never judged against the one-alive rule, its chain laid
    /// out behind it. Null for unregistered content or a full hard cap.</summary>
    public Sandworm? SpawnMirror(Vector3 head, float facing)
    {
        if (_type == null || _caps.ForcedRoom <= 0) return null;
        var worm = new Sandworm(_type, head, facing);
        worm.MarkMirror();
        _worms.Add(worm);
        return worm;
    }

    /// <summary>Whether a worm is alive (the summon item refuses a second).</summary>
    public bool HasLiving => LivingCount > 0;

    /// <summary>
    /// Whether the thumper may wake a worm at the solid cell (<paramref name="x"/>, <paramref name="y"/>,
    /// <paramref name="z"/>): none alive, room under the hard cap, the cell loaded desert sand or sandstone.
    /// </summary>
    public bool CanSummonAt(ChunkManager chunks, int x, int y, int z) =>
        _type != null && !HasLiving && _caps.ForcedRoom > 0
        && chunks.TryGetCell(x, y, z, out var id, out _) && SandwormGround.IsSand(id)
        && SandwormGround.IsDesert(chunks, x + 0.5f, z + 0.5f);

    /// <summary>Wakes a worm under the sand at that cell when <see cref="CanSummonAt"/> allows; it begins in its
    /// thump. Null when refused.</summary>
    public Sandworm? Summon(ChunkManager chunks, int x, int y, int z)
    {
        if (!CanSummonAt(chunks, x, y, z)) return null;
        var surface = SandwormGround.At(chunks, x + 0.5f, z + 0.5f).SurfaceY;
        return Spawn(new Vector3(x + 0.5f, surface - SandwormConfig.BurrowDepthBlocks, z + 0.5f));
    }

    /// <summary>A client's network reconcile: the server no longer reports this worm, so it plays its death.</summary>
    public bool Remove(Sandworm worm)
    {
        if (!_worms.Contains(worm)) return false;
        worm.BeginDying();
        return true;
    }

    /// <summary>Advances every client mirror (a client never ticks the AI): the head dead-reckons along the
    /// server's velocity and the chain follows, over the sand of <paramref name="chunks"/>; a mirror whose death
    /// has played is dropped.</summary>
    public void AnimateMirrors(float deltaTime, ChunkManager chunks)
    {
        for (var i = _worms.Count - 1; i >= 0; i--)
        {
            _worms[i].AnimateMirrorFrame(deltaTime, (x, z) => SandwormGround.At(chunks, x, z));
            if (_worms[i].IsRemovable) _worms.RemoveAt(i);
        }
    }

    /// <summary>The frame: hunts, bites, loots and retires.</summary>
    public void Update(float deltaTime, ChunkManager chunks, DropManager drops, IReadOnlyList<IMobTarget> targets,
        BlockDebrisSystem? debris = null)
    {
        DamageDealtThisFrame = 0;
        if (chunks == null || targets == null) return;
        for (var i = _worms.Count - 1; i >= 0; i--)
        {
            var worm = _worms[i];
            UpdateWorm(worm, deltaTime, chunks, targets);
            worm.UpdateHazards(deltaTime, chunks);

            if (worm.IsDying)
            {
                if (worm.IsRemovable) _worms.RemoveAt(i);
                continue;
            }

            if (worm.IsDead)
            {
                DropLoot(worm, chunks, drops);
                worm.BeginDying();
                continue;
            }

            if (worm.Brain.IsFinished)
            {
                // It sank away for good: gone without loot
                worm.BeginDying();
                _worms.RemoveAt(i);
                continue;
            }

            Bite(worm, targets);
        }
    }

    private void UpdateWorm(Sandworm worm, float deltaTime, ChunkManager chunks, IReadOnlyList<IMobTarget> targets)
    {
        var target = PickTarget(worm, chunks, targets);
        if (worm.Phase != SandwormPhase.Thump && worm.Phase != SandwormPhase.Retreat
            && ((target == null && TargetsAllFar(worm, targets))
                || !SandwormGround.IsNearDesert(chunks, worm.Position.X, worm.Position.Z, SandwormConfig.DesertLeaveBlocks)))
            worm.Brain.BeginRetreat();
        worm.Update(deltaTime, target, (x, z) => SandwormGround.At(chunks, x, z));
    }

    // The nearest desert target within hunt range, preferring one a hit can hurt over an immune one (which a
    // hovering camera still gets chased by when it is alone)
    private static Vector3? PickTarget(Sandworm worm, ChunkManager chunks, IReadOnlyList<IMobTarget> targets)
    {
        IMobTarget? best = null;
        var bestImmune = true;
        var bestDistance = float.MaxValue;
        var range = SandwormConfig.HuntRadiusBlocks * SandwormConfig.HuntRadiusBlocks;
        for (var i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            if (target.IsDead) continue;
            var eye = target.Eye;
            var dx = eye.X - worm.Position.X;
            var dz = eye.Z - worm.Position.Z;
            var distance = dx * dx + dz * dz;
            if (distance > range || !SandwormGround.IsDesert(chunks, eye.X, eye.Z)) continue;
            if (best != null && (bestImmune == target.IsImmune ? distance >= bestDistance : !bestImmune)) continue;
            best = target;
            bestImmune = target.IsImmune;
            bestDistance = distance;
        }

        return best == null ? null : best.Eye - new Vector3(0f, Camera.EyeHeight * 0.5f, 0f);
    }

    // With nobody left near enough, a worm gives up at once (a target merely out of the desert only starts the
    // brain's idle clock, which retreats later)
    private static bool TargetsAllFar(Sandworm worm, IReadOnlyList<IMobTarget> targets)
    {
        if (targets.Count == 0) return false;
        var limit = SandwormConfig.DespawnRadiusBlocks * SandwormConfig.DespawnRadiusBlocks;
        return MobTargets.MinHorizontalDistanceSquared(targets, worm.Position) > limit;
    }

    // The chomp and the dash's contact: every non-immune target the contact touches takes the hit, then the worm rests
    private void Bite(Sandworm worm, IReadOnlyList<IMobTarget> targets)
    {
        if (!worm.CanHit || worm.Brain.Contact == SandwormContact.None) return;
        var chomp = worm.Brain.Contact == SandwormContact.Chomp;
        var landed = false;
        for (var i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            if (target.IsImmune || target.IsDead || !worm.Touches(target.Eye, out var from)) continue;
            var away = new Vector3(target.Eye.X - from.X, 0f, target.Eye.Z - from.Z);
            if (away.LengthSquared() < 0.01f)
                away = new Vector3(MathF.Sin(worm.Facing), 0f, MathF.Cos(worm.Facing));
            var damage = chomp ? SandwormConfig.ChompDamage : SandwormConfig.DashDamage;
            DamageDealtThisFrame += damage;
            target.TakeHit(damage, away, chomp ? SandwormConfig.ChompKnockback : SandwormConfig.DashKnockback,
                chomp ? SandwormConfig.ChompLift : 0f, worm.Type.DisplayName);
            landed = true;
        }

        if (landed) worm.NoteHit();
    }

    private void DropLoot(Sandworm worm, ChunkManager chunks, DropManager drops)
    {
        if (worm.Type.LootTable == null) return;
        _loot.Clear();
        var (x, y, z) = worm.Position.BlockCell();
        worm.Type.LootTable.Roll(new LootContext(x, y, z, 0), _loot);
        // The head may be under the sand when it dies: the loot is tossed from above the surface
        var origin = worm.Position with { Y = MathF.Max(worm.Position.Y, SandwormGround.At(chunks, worm.Position.X, worm.Position.Z).SurfaceY) };
        foreach (var id in _loot)
            drops.SpawnAt(id, origin, MobLoot.RandomToss());
    }
}
