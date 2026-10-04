namespace silkgltriangle;

/// <summary>
/// Serialises a live <see cref="Mob"/> into its <see cref="EntitySaveData"/> form. Extracted
/// from <see cref="World"/> so the per-kind ammo handling is a pure function of the mob, not a
/// detail of the world's save loop.
/// </summary>
internal static class MobSaveCodec
{
    public static EntitySaveData.MobData Capture(Mob mob, int? ammo) => new()
    {
        Type = mob.Type.Name,
        Position = [mob.Position.X, mob.Position.Y, mob.Position.Z],
        Health = mob.Health,
        Facing = mob.Facing,
        Velocity = [mob.Velocity.X, mob.Velocity.Y, mob.Velocity.Z],
        Ammo = ammo
    };
}
