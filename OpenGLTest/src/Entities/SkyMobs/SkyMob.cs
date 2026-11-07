using System.Numerics;

namespace VoxelEngine.Entities.SkyMobs;

/// <summary>
/// A mob of the sky islands (<see cref="Galeraptor"/>, <see cref="Cloudgolem"/>): a <see cref="RealmMob"/>
/// whose health and damage are scaled by the tier of the island zone it spawned in
/// (<see cref="SkyDifficulty"/>, read once from the spawn column, so a save restore and a client's mirror at
/// the same spot get the same numbers). Main thread only.
/// </summary>
public abstract class SkyMob : RealmMob
{
    protected SkyMob(EntityType type, Vector3 position, float moveReferenceSpeed, bool spawnIn,
        FlightTuning? flight = null)
        : base(type, position, moveReferenceSpeed, spawnIn, flight)
    {
        Tier = SkyDifficulty.TierAt(position.X, position.Z);
        Health = MaxHealth;
    }

    /// <summary>The tier of the island zone this mob spawned in, fixed for life.</summary>
    public int Tier { get; }

    /// <inheritdoc />
    public override int MaxHealth => SkyDifficulty.Scale(Type.MaxHealth, Tier);
}
