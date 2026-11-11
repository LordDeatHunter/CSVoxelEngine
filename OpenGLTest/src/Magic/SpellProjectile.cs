using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Spells;
using VoxelEngine.Entities;
using VoxelEngine.Player;

namespace VoxelEngine.Magic;

/// <summary>
/// One spell bolt in flight, GL-free, main thread only. It flies straight from just in front of the
/// caster's eye at <see cref="SpellConfig.ProjectileSpeed"/>, with no gravity, in sub-steps of at most
/// <see cref="SpellConfig.ProjectileMaxStepBlocks"/> so a slow frame never carries it past a thin layer.
/// It is stopped by a solid cell (the same <see cref="BoxSweep"/> axis moves a thrown item uses), by the
/// first non-air cell its center enters (so it lands on water, lava, leaves and plants instead of passing
/// through them), or by a mob's body (<see cref="ThrownProjectile.FindMobHit"/>), and it is spent once it
/// has flown <see cref="SpellImpactRules.RangeFor"/> blocks.
/// </summary>
internal sealed class SpellProjectile
{
    private readonly Vector3 _direction;

    /// <summary>A bolt cast by <paramref name="caster"/> from <paramref name="eye"/> along <paramref name="direction"/>.</summary>
    public SpellProjectile(PlayerContext caster, SpellPart element, SpellStats stats, Vector3 eye, Vector3 direction)
    {
        _direction = direction.LengthSquared() < 1e-8f ? -Vector3.UnitZ : Vector3.Normalize(direction);
        Caster = caster;
        Element = element;
        Stats = stats;
        Origin = eye + _direction * SpellConfig.ProjectileLaunchOffsetBlocks;
        Position = Origin;
        Velocity = _direction * SpellConfig.ProjectileSpeed;
        Range = SpellImpactRules.RangeFor(stats);
    }

    /// <summary>Who cast it.</summary>
    public PlayerContext Caster { get; }

    /// <summary>The element it applies where it lands.</summary>
    public SpellPart Element { get; }

    /// <summary>The recipe's compiled stats (damage and range factors).</summary>
    public SpellStats Stats { get; }

    /// <summary>Where it started; a mob it hits is shoved away from here.</summary>
    public Vector3 Origin { get; }

    /// <summary>The center of its collision box.</summary>
    public Vector3 Position { get; private set; }

    /// <summary>Its velocity in blocks per second, constant over the flight.</summary>
    public Vector3 Velocity { get; }

    /// <summary>How far it has flown, in blocks.</summary>
    public float Traveled { get; private set; }

    /// <summary>How far it flies before it is spent, in blocks.</summary>
    public float Range { get; }

    /// <summary>The cell it landed on (integer coordinates), valid after <see cref="SpellFlight.HitCell"/>.</summary>
    public Vector3 ImpactCell { get; private set; }

    /// <summary>The unit normal of the face it landed on, pointing back toward where it came from.</summary>
    public Vector3 ImpactNormal { get; private set; }

    /// <summary>The mob it touched, valid after <see cref="SpellFlight.HitMob"/>.</summary>
    public Mob? HitMob { get; private set; }

    /// <summary>Seconds until its next cosmetic trail spark, kept by the owner.</summary>
    public float SparkTimer { get; internal set; }

    /// <summary>The sRGB color of its cosmetic sparks, chosen once by the owner.</summary>
    public Vector3 SparkColor { get; init; }

    /// <summary>Flies the bolt for <paramref name="deltaTime"/> seconds and reports what stopped it, if anything.</summary>
    public SpellFlight Update(float deltaTime, ChunkManager chunks, IReadOnlyList<Mob>? mobs)
    {
        var remaining = Math.Max(0f, deltaTime) * SpellConfig.ProjectileSpeed;
        var half = new Vector3(SpellConfig.ProjectileHalfExtent);
        while (remaining > 0f)
        {
            var step = MathF.Min(remaining, SpellConfig.ProjectileMaxStepBlocks);
            remaining -= step;
            var movement = _direction * step;

            var next = BoxSweep.MoveOnAxis(Position, movement.X, 0, half, chunks, out var hitX);
            next = BoxSweep.MoveOnAxis(next, movement.Z, 2, half, chunks, out var hitZ);
            next = BoxSweep.MoveOnAxis(next, movement.Y, 1, half, chunks, out var hitY);
            Position = next;

            if (hitX) return LandOnBlockedAxis(0, movement.X);
            if (hitZ) return LandOnBlockedAxis(2, movement.Z);
            if (hitY) return LandOnBlockedAxis(1, movement.Y);

            var cellX = (int)MathF.Floor(Position.X);
            var cellY = (int)MathF.Floor(Position.Y);
            var cellZ = (int)MathF.Floor(Position.Z);
            if (chunks.TryGetCell(cellX, cellY, cellZ, out var id, out _) && id != 0)
            {
                ImpactCell = new Vector3(cellX, cellY, cellZ);
                ImpactNormal = DominantAxisNormal();
                return SpellFlight.HitCell;
            }

            HitMob = ThrownProjectile.FindMobHit(Position, SpellConfig.ProjectileHalfExtent, mobs);
            if (HitMob != null) return SpellFlight.HitMob;

            Traveled += step;
            if (Traveled >= Range) return SpellFlight.Spent;
        }
        return SpellFlight.Flying;
    }

    /// <summary>
    /// The solid cell that blocked a move along <paramref name="axis"/> is just past the box's leading face on
    /// that axis; the face it landed on looks back the way the bolt came. The sweep stops up to one
    /// <see cref="BoxSweep.StepSize"/> short of the cell, so the probe reaches that far past the face.
    /// </summary>
    private SpellFlight LandOnBlockedAxis(int axis, float delta)
    {
        var sign = MathF.Sign(delta);
        var probe = BoxSweep.OffsetAxis(Position, axis, sign * (SpellConfig.ProjectileHalfExtent + BoxSweep.StepSize));
        ImpactCell = new Vector3(MathF.Floor(probe.X), MathF.Floor(probe.Y), MathF.Floor(probe.Z));
        ImpactNormal = BoxSweep.OffsetAxis(Vector3.Zero, axis, -sign);
        return SpellFlight.HitCell;
    }

    /// <summary>The unit normal against the flight's dominant axis: the face a bolt entering a cell most likely crossed.</summary>
    private Vector3 DominantAxisNormal()
    {
        var abs = Vector3.Abs(_direction);
        if (abs.X >= abs.Y && abs.X >= abs.Z) return new Vector3(-MathF.Sign(_direction.X), 0f, 0f);
        if (abs.Z >= abs.Y) return new Vector3(0f, 0f, -MathF.Sign(_direction.Z));
        return new Vector3(0f, -MathF.Sign(_direction.Y), 0f);
    }
}
