using VoxelEngine.Entities;
using VoxelEngine.Entities.TowerMobs;

namespace VoxelEngine.Saves;

/// <summary>
/// Serializes a live <see cref="Mob"/> into its <see cref="EntitySaveData"/> form. Extracted
/// from <see cref="World"/> so the per-kind fields (a goblin's ammo, a slime's size, a skeleton's tower floor and
/// outfit, a Skeleton King's tower) are a pure function of the mob, not a detail of the world's save loop.
/// </summary>
internal static class MobSaveCodec
{
    /// <summary>The save form of <paramref name="mob"/>. A skeleton's <paramref name="floor"/> fills both the tower cell and
    /// the floor; without one, <paramref name="tower"/> (a Skeleton King's tower) fills the tower cell alone.</summary>
    public static EntitySaveData.MobData Capture(Mob mob, int? ammo, int? size = null, int? torches = null, int? tier = null,
        TowerFloorRef? floor = null, (int CellX, int CellZ)? tower = null, int? outfit = null) => new()
    {
        Type = mob.Type.Name,
        Position = [mob.Position.X, mob.Position.Y, mob.Position.Z],
        Health = mob.Health,
        Facing = mob.Facing,
        Velocity = [mob.Velocity.X, mob.Velocity.Y, mob.Velocity.Z],
        Ammo = ammo,
        Size = size,
        Torches = torches,
        Tier = tier,
        TowerCell = floor is { } f ? [f.CellX, f.CellZ] : tower is { } t ? [t.CellX, t.CellZ] : null,
        TowerFloor = floor?.Floor,
        Outfit = outfit is > 0 ? outfit : null
    };
}
