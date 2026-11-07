using VoxelEngine.Interaction;
using VoxelEngine.Items;
using VoxelEngine.Player;

namespace VoxelEngine.Networking.ServerSide;

/// <summary>
/// How often a player may land a left-click attack, the server-side twin of the client's own
/// pacing: a punch once per the held item's mining pace (<see cref="Mining.HitGapSeconds"/>, the
/// same timer the client's punch runs on), a slice swing once per swing
/// (<see cref="PlayerModel.SliceDuration"/>: a buffered return swing only starts when the previous
/// one finished), and a sword swing once the swing before it has played out
/// (<see cref="PlayerModel.SwordDuration"/> of the previous swing, each of the chain's having its
/// own length). GL-free.
/// </summary>
public static class AttackPacing
{
    /// <summary>The shortest time between an attack of <paramref name="previous"/> and one of
    /// <paramref name="next"/> with <paramref name="held"/> in hand.</summary>
    public static float MinGapSeconds(NetAttackKind previous, NetAttackKind next, Item? held, float speedFactor = 1f)
    {
        if (next == NetAttackKind.Punch) return Mining.HitGapSeconds(held, speedFactor);
        if (!NetAttacks.IsSword(next)) return PlayerModel.SliceDuration;
        // A sword swing starts when the swing before it in the chain has finished: the previous attack's own
        // length when that was a sword swing, and the shortest swing's when the last attack was something else
        // (a punch or a slice with another item in hand), which no chain can beat.
        return NetAttacks.IsSword(previous)
            ? PlayerModel.SwordDuration(NetAttacks.SwordSwingOf(previous))
            : PlayerModel.ShortestSwordSwingSeconds;
    }
}
