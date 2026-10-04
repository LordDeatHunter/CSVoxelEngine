using System.Numerics;

namespace silkgltriangle;

/// <summary>What a <see cref="NetEntity"/> entry is.</summary>
public enum NetEntityKind : byte
{
    Slime = 1,
    Goblin = 2,
    Drop = 3,
}

/// <summary>
/// One server-authoritative entity in a broadcast snapshot: its kind and stable id, and the state
/// the client renders from. A mob uses <see cref="Health"/>, <see cref="Yaw"/> and
/// <see cref="Velocity"/> (and <see cref="Ammo"/> for a goblin); a drop uses <see cref="ItemId"/>.
/// GL-free and content-free.
/// </summary>
public readonly record struct NetEntity(NetEntityKind Kind, int Id, Vector3 Position, float Yaw,
    int Health, int Ammo, byte ItemId, Vector3 Velocity);
