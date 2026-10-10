using System.Numerics;

namespace silkgltriangle;

/// <summary>What a <see cref="NetEntity"/> entry is.</summary>
public enum NetEntityKind : byte
{
    Slime = 1,
    Goblin = 2,
    Drop = 3,
    PrimedTnt = 4,
    FallingBlock = 5,
}

/// <summary>
/// One server-authoritative entity in a broadcast snapshot: its kind and stable id, and the state
/// the client renders from. A mob uses <see cref="Health"/>, <see cref="Yaw"/> and
/// <see cref="Velocity"/>, and <see cref="Ammo"/> carries a goblin's ammo or a slime's size
/// (<see cref="Slime.Size"/>; a 0 from an older server reads as the smallest size); a drop uses
/// <see cref="ItemId"/>.
/// A primed TNT uses <see cref="ItemId"/> (the block that primed), <see cref="Health"/> (the fuse
/// remaining, in milliseconds) and <see cref="Ammo"/> (the whole fuse, in milliseconds); a falling
/// block uses <see cref="ItemId"/> (the block that toppled). GL-free and content-free.
/// </summary>
public readonly record struct NetEntity(NetEntityKind Kind, int Id, Vector3 Position, float Yaw,
    int Health, int Ammo, byte ItemId, Vector3 Velocity)
{
    /// <summary>Whether <paramref name="kind"/> is a mob (a slime or a goblin), the only kinds the
    /// client's mob reconcile spawns; drops, primed TNT and falling blocks are mirrored apart.</summary>
    public static bool IsMob(NetEntityKind kind) => kind is NetEntityKind.Slime or NetEntityKind.Goblin;
}

/// <summary>One server cell change: world coordinates and the new block id and state.</summary>
public readonly record struct NetCellChange(int X, int Y, int Z, byte Id, byte State);
