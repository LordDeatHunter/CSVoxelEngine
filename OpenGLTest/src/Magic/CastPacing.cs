namespace VoxelEngine.Magic;

/// <summary>
/// One caster's cast cooldown: two casts are at least <see cref="SpellConfig.CastCooldownSeconds"/>
/// apart. The clock is passed in (the world's update time, or a server's elapsed time), so the rule
/// needs no wall clock. A server forgives a little network jitter through the slack; the local player
/// passes none.
/// </summary>
public sealed class CastPacing
{
    private double _lastCastAt = double.NegativeInfinity;

    /// <summary>
    /// Whether a cast at <paramref name="now"/> keeps the cooldown, less <paramref name="slackSeconds"/>.
    /// Only asks: it never records the cast.
    /// </summary>
    public bool IsReady(double now, double slackSeconds) =>
        now - _lastCastAt + slackSeconds >= SpellConfig.CastCooldownSeconds;

    /// <summary>Records a cast at <paramref name="now"/>, which the next one is measured from.</summary>
    public void Mark(double now) => _lastCastAt = now;
}
