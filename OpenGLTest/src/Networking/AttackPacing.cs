namespace silkgltriangle;

/// <summary>
/// How often a player may land a left-click attack, the server-side twin of the client's own
/// pacing: a punch once per the held item's mining pace (<see cref="Mining.HitGapSeconds"/>, the
/// same timer the client's punch runs on), a slice swing once per swing
/// (<see cref="PlayerModel.SliceDuration"/>: a buffered return swing only starts when the previous
/// one finished). GL-free.
/// </summary>
public static class AttackPacing
{
    /// <summary>The shortest time between two attacks of <paramref name="kind"/> with
    /// <paramref name="held"/> in hand.</summary>
    public static float MinGapSeconds(NetAttackKind kind, Item? held) =>
        kind == NetAttackKind.Punch ? Mining.HitGapSeconds(held) : PlayerModel.SliceDuration;
}
