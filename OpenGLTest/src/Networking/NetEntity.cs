using System;
using System.Numerics;
using VoxelEngine.Entities.Icebergs;
using VoxelEngine.Entities.Slimes;

namespace VoxelEngine.Networking;

/// <summary>What a <see cref="NetEntity"/> entry is.</summary>
public enum NetEntityKind : byte
{
    Slime = 1,
    Goblin = 2,
    Drop = 3,
    PrimedTnt = 4,
    FallingBlock = 5,
    Iceberg = 6,
    Hellbrute = 7,
    Cinderwitch = 8,
}

/// <summary>
/// One server-authoritative entity in a broadcast snapshot: its kind and stable id, and the state
/// the client renders from. A mob uses <see cref="Health"/>, <see cref="Yaw"/> and
/// <see cref="Velocity"/>, and <see cref="Ammo"/> carries a goblin's ammo or a slime's size
/// (<see cref="Slime.Size"/>; a 0 from an older server reads as the smallest size); a drop uses
/// <see cref="ItemId"/>.
/// A primed TNT uses <see cref="ItemId"/> (the block that primed), <see cref="Health"/> (the fuse
/// remaining, in milliseconds) and <see cref="Ammo"/> (the whole fuse, in milliseconds); a falling
/// block uses <see cref="ItemId"/> (the block that toppled). An iceberg uses <see cref="Position"/>
/// (its waterline center), <see cref="Yaw"/> (its quarter turns times pi/2,
/// <see cref="TurnsFromYaw"/>), <see cref="Health"/> (its seed), <see cref="Ammo"/> (its scale and
/// sinking, <see cref="PackIcebergAmmo"/>), <see cref="ItemId"/> (its variant) and
/// <see cref="Velocity"/> (its drift, which the client dead-reckons along). Every entry ends with
/// <see cref="Effects"/>, a mob's active status effects as a bitmask of each effect's
/// <c>StatusEffect.Bit</c> (0 for every other kind); the client drops the bits no loaded effect owns.
/// GL-free and content-free.
/// </summary>
public readonly record struct NetEntity(NetEntityKind Kind, int Id, Vector3 Position, float Yaw,
    int Health, int Ammo, byte ItemId, Vector3 Velocity, byte Effects = 0)
{
    /// <summary>Whether <paramref name="kind"/> is a mob (a slime, a goblin or a hell mob), the only kinds
    /// the client's mob reconcile spawns; drops, primed TNT and falling blocks are mirrored apart.</summary>
    public static bool IsMob(NetEntityKind kind) =>
        kind is NetEntityKind.Slime or NetEntityKind.Goblin or NetEntityKind.Hellbrute or NetEntityKind.Cinderwitch;

    // An iceberg's scale in thousandths fills the low 16 bits of Ammo, its sinking (0..255) the next 8
    private const float IcebergScaleUnits = 1000f;
    private const int IcebergScaleMask = 0xFFFF;
    private const int IcebergSinkShift = 16;
    private const float IcebergSinkUnits = 255f;

    /// <summary>An iceberg's <see cref="Iceberg.Scale"/> and <see cref="Iceberg.Sink01"/> packed into <see cref="Ammo"/>.</summary>
    public static int PackIcebergAmmo(float scale, float sink01)
    {
        var scaleUnits = Math.Clamp((int)MathF.Round(scale * IcebergScaleUnits), 0, IcebergScaleMask);
        var sinkUnits = (int)MathF.Round(Math.Clamp(sink01, 0f, 1f) * IcebergSinkUnits);
        return scaleUnits | (sinkUnits << IcebergSinkShift);
    }

    /// <summary>The scale and sinking <see cref="PackIcebergAmmo"/> packed into <paramref name="ammo"/>.</summary>
    public static void UnpackIcebergAmmo(int ammo, out float scale, out float sink01)
    {
        scale = (ammo & IcebergScaleMask) / IcebergScaleUnits;
        sink01 = ((ammo >> IcebergSinkShift) & 0xFF) / IcebergSinkUnits;
    }

    /// <summary>An iceberg's quarter turns as the <see cref="Yaw"/> it travels in.</summary>
    public static float YawFromTurns(int turns) => (turns & 3) * MathF.PI * 0.5f;

    /// <summary>The quarter turns (0..3) an iceberg entry's <paramref name="yaw"/> carries.</summary>
    public static int TurnsFromYaw(float yaw) => (int)MathF.Round(yaw / (MathF.PI * 0.5f)) & 3;
}

/// <summary>One server cell change: world coordinates and the new block id and state.</summary>
public readonly record struct NetCellChange(int X, int Y, int Z, byte Id, byte State);
