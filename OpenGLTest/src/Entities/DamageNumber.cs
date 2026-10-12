using System.Numerics;

namespace VoxelEngine.Entities;

/// <summary>
/// One floating damage number in a <see cref="DamageNumberPool"/>: where it started, the damage it
/// shows, how long it has lived and the horizontal direction it drifts while it rises. An inactive
/// entry is a free slot.
/// </summary>
public struct DamageNumber
{
    /// <summary>World position it appeared at, above the struck mob's healthbar.</summary>
    public Vector3 Origin;

    /// <summary>The damage it shows; always above 0 while active.</summary>
    public int Amount;

    /// <summary>Seconds since it appeared.</summary>
    public float Age;

    /// <summary>Unit XZ direction it drifts in as it rises.</summary>
    public Vector2 Drift;

    public bool Active;
}
