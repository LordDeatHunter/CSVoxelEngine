using System.Numerics;

namespace VoxelEngine.Entities.CaveMobs;

/// <summary>
/// A mob of the overworld's cave sub-biomes (<see cref="Shardling"/>, <see cref="Sporeling"/>,
/// <see cref="Magmaborn"/>): a <see cref="RealmMob"/> whose health and damage are scaled by its depth tier
/// (<see cref="CaveDifficulty"/>). The tier is handed in when it spawns (a ring spawn reads it from its floor, a save
/// restore and a client's mirror take the one saved or sent) and is fixed for life. Main thread only.
/// </summary>
public abstract class CaveMob : RealmMob
{
    protected CaveMob(EntityType type, Vector3 position, int tier, float moveReferenceSpeed, bool spawnIn)
        : base(type, position, moveReferenceSpeed, spawnIn)
    {
        Tier = CaveDifficulty.Clamp(tier);
        Health = MaxHealth;
    }

    /// <summary>The depth tier this mob spawned with, 0..<see cref="MobConfig.CaveMaxTier"/>, fixed for life.</summary>
    public int Tier { get; }

    /// <inheritdoc />
    public override int MaxHealth => CaveDifficulty.Scale(Type.MaxHealth, Tier);
}
