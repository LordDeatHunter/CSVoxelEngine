using System;
using System.Numerics;
using VoxelEngine.Entities.Icebergs;
using VoxelEngine.Entities.Slimes;
using VoxelEngine.Entities.Wildlife;

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
    Galeraptor = 9,
    Cloudgolem = 10,
    Sandworm = 11,
    SandwormTarget = 12,
    Skeleton = 13,
    SkeletonKing = 14,
    Shardling = 15,
    Sporeling = 16,
    Magmaborn = 17,

    // The animals of the wild, in WildlifeKind order from Deer on (NetEntity.KindOf)
    Deer = 18,
    Boar = 19,
    DireBoar = 20,
    Crow = 21,
    Dragonfly = 22,

    // The crawlers sit clear of the animals' run, which grows with every new species
    Spider = 64,
    SpiderBroodmother = 65,
    Scorpion = 66,
}

/// <summary>
/// One server-authoritative entity in a broadcast snapshot: its kind and stable id, and the state
/// the client renders from. A mob uses <see cref="Health"/>, <see cref="Yaw"/> and
/// <see cref="Velocity"/>, and <see cref="Ammo"/> carries a goblin's ammo or a slime's size
/// (<see cref="Slime.Size"/>; a 0 from an older server reads as the smallest size) or a hell or sky mob's
/// attack count (<c>RealmMob.AttackCount</c>: a client's mirror plays its attack clip whenever the count moves,
/// the count's remainder modulo the kind's attack variants naming the clip, and an older server's 0 never moves it);
/// <see cref="ItemId"/> is a goblin's torches or a skeleton's tower tier (0..<c>MobConfig.TowerMaxTier</c>, clamped on
/// read, since a mirror cannot derive it from where it stands) with its role and outfit packed above it
/// (<c>Skeleton.PackLook</c>) or the Skeleton King's fight phase (1..3, clamped on read;
/// its <see cref="Ammo"/> is its attack count, naming slam, volley and summon by its remainder modulo 3) or a cave mob's
/// depth tier (0..<c>MobConfig.CaveMaxTier</c>, clamped on read, fixed at its spawn; its <see cref="Ammo"/> is its attack
/// count; a spider or scorpion packs the face it clings to above the tier, <c>CrawlerMob.PackStance</c>, and its <see cref="Yaw"/> is its
/// heading's angle on that face) or a wild animal's posture (<c>Animal.Posture</c>: whether a flyer sits on the ground,
/// which a mirror cannot tell from its velocity); a drop uses
/// <see cref="ItemId"/>.
/// A sandworm is a mob entry for its head (<see cref="Position"/>, <see cref="Yaw"/> its heading, <see cref="Velocity"/> the
/// head velocity a client dead-reckons along) whose <see cref="Ammo"/> is its phase (<c>SandwormPhase</c>); its body is
/// not on the wire, since the client drags its own chain after the head. While it digs toward a surfacing point a
/// <see cref="NetEntityKind.SandwormTarget"/> entry with the worm's id follows its head: <see cref="Position"/> the
/// point and <see cref="Health"/> how near it is in thousandths (the sand telegraph).
/// A primed TNT uses <see cref="ItemId"/> (the block that primed), <see cref="Health"/> (the fuse
/// remaining, in milliseconds) and <see cref="Ammo"/> (the whole fuse, in milliseconds); a falling
/// block uses <see cref="ItemId"/> (the block that toppled). An iceberg uses <see cref="Position"/>
/// (its waterline center), <see cref="Yaw"/> (its quarter turns times pi/2,
/// <see cref="TurnsFromYaw"/>), <see cref="Health"/> (its seed), <see cref="Ammo"/> (its scale and
/// sinking, <see cref="PackIcebergAmmo"/>), <see cref="ItemId"/> (its variant) and
/// <see cref="Velocity"/> (its drift, which the client dead-reckons along). Every entry ends with
/// <see cref="Effects"/>, a mob's active status effects as a bitmask of each effect's
/// <c>StatusEffect.Bit</c> (0 for every other kind); the client drops the bits no loaded effect owns.
/// <see cref="ItemId"/> is a block or item id wide enough for any id; the mob data it carries for a mob kind
/// fits in a byte, but for a crawler's surface in the byte above. GL-free and content-free.
/// </summary>
public readonly record struct NetEntity(NetEntityKind Kind, int Id, Vector3 Position, float Yaw,
    int Health, int Ammo, ushort ItemId, Vector3 Velocity, byte Effects = 0)
{
    /// <summary>Whether <paramref name="kind"/> is a mob (a slime, a goblin, a hell, sky, cave or tower mob or a wild
    /// animal), the only kinds the client's mob reconcile spawns; drops, primed TNT and falling blocks are mirrored apart.</summary>
    public static bool IsMob(NetEntityKind kind) =>
        kind is NetEntityKind.Slime or NetEntityKind.Goblin or NetEntityKind.Hellbrute or NetEntityKind.Cinderwitch
            or NetEntityKind.Galeraptor or NetEntityKind.Cloudgolem or NetEntityKind.Sandworm or NetEntityKind.Skeleton
            or NetEntityKind.SkeletonKing or NetEntityKind.Shardling or NetEntityKind.Sporeling or NetEntityKind.Magmaborn
            or NetEntityKind.Spider or NetEntityKind.SpiderBroodmother or NetEntityKind.Scorpion
        || TryGetWildlife(kind, out _);

    /// <summary>The entry kind of a wild animal of <paramref name="species"/>.</summary>
    public static NetEntityKind KindOf(WildlifeKind species) => (NetEntityKind)((int)NetEntityKind.Deer + (int)species);

    /// <summary>The wild animal an entry <paramref name="kind"/> stands for; false for every other kind.</summary>
    public static bool TryGetWildlife(NetEntityKind kind, out WildlifeKind species)
    {
        var index = (int)kind - (int)NetEntityKind.Deer;
        species = (WildlifeKind)Math.Max(0, index);
        return index >= 0 && index < WildlifeSpecies.Names.Length;
    }

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
public readonly record struct NetCellChange(int X, int Y, int Z, ushort Id, byte State);
