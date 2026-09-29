using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// A living hostile entity: the state every mob kind shares (its <see cref="EntityType"/>,
/// body centre, health, facing and hit flash) plus the body dimensions the targeting, projectile
/// and healthbar code reads, so those paths serve every mob kind through one list instead of one
/// code path per kind. Main thread only.
/// </summary>
public abstract class Mob
{
    /// <summary>Hit flash amount, snapped to 1 by <see cref="TakeDamage"/>; each subclass fades it.</summary>
    protected float _hitFlash;

    protected Mob(EntityType type, Vector3 position)
    {
        Type = type;
        Position = position;
        Health = type.MaxHealth;
    }

    public EntityType Type { get; }

    /// <summary>Body centre.</summary>
    public Vector3 Position { get; protected set; }

    public int Health { get; protected set; }

    public bool IsDead => Health <= 0;

    /// <summary>Yaw in radians the body faces; the model faces -Z at 0.</summary>
    public float Facing { get; protected set; }

    /// <summary>Hit flash amount, 1 right after <see cref="TakeDamage"/> fading back to 0. The
    /// entity pass tints the skin red by this (non-emissive).</summary>
    public float HitFlash => _hitFlash;

    /// <summary>Horizontal half extent of the collision body, used by touch and hit tests.</summary>
    public abstract float BodyHalfExtent { get; }

    /// <summary>Vertical half extent of the collision body about <see cref="Position"/>.</summary>
    public abstract float BodyHalfHeight { get; }

    /// <summary>Height of the top of the drawn body above <see cref="Position"/>, where the
    /// healthbar is placed.</summary>
    public abstract float HeadAboveCentre { get; }

    /// <summary>Applies a hit; the owner drops the loot and removes the mob once dead.</summary>
    public virtual void TakeDamage(int amount)
    {
        if (amount <= 0) return;
        Health = Math.Max(0, Health - amount);
        _hitFlash = 1f;
    }

    /// <summary>Sets the horizontal shove a contact or hit imparts, leaving vertical motion
    /// alone. Setting (not adding) keeps a sustained touch from accelerating the mob.</summary>
    public abstract void Shove(Vector3 horizontalDirection, float speed);
}
