using System.Numerics;

namespace VoxelEngine.Entities.HellMobs;

/// <summary>
/// A mob of hell's cavern pockets (<see cref="Hellbrute"/>, <see cref="Cinderwitch"/>): a
/// <see cref="RealmMob"/> whose health, damage and attack pace are scaled by the hell layer it
/// spawned in (<see cref="LayerDifficulty"/>, read once from the spawn height, so a save restore and
/// a client's mirror at the same spot get the same numbers). Main thread only.
/// </summary>
public abstract class HellMob : RealmMob
{
    protected HellMob(EntityType type, Vector3 position, float walkSpeed, bool spawnIn)
        : base(type, position, walkSpeed, spawnIn)
    {
        Layer = LayerDifficulty.LayerAt(position.Y);
        Health = MaxHealth;
    }

    /// <summary>The hell layer this mob spawned in, fixed for life.</summary>
    public int Layer { get; }

    /// <inheritdoc />
    public override int MaxHealth => LayerDifficulty.Scale(Type.MaxHealth, Layer);
}
