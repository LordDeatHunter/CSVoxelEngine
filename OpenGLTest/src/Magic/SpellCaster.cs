using System;
using VoxelEngine.Config;
using VoxelEngine.Player;

namespace VoxelEngine.Magic;

/// <summary>
/// The one seam every cast passes, owned by <see cref="World"/>; main thread only. It adds the
/// playing gate to <see cref="CastRules"/> and fires the <see cref="Effect"/> hook for an accepted
/// cast (or backfires a fizzle), then the cosmetic <see cref="Feedback"/> hook. A client only predicts:
/// it checks and paces, never spends mana and never fires the effect, so a client never edits a cell or
/// damages a mob; its server replays the cast and bills it. The client's feedback still runs, as a
/// prediction that never fizzles (the roll is the server's).
/// </summary>
internal sealed class SpellCaster
{
    private readonly World _world;
    private readonly bool _clientMode;

    internal SpellCaster(World world, bool clientMode)
    {
        _world = world;
        _clientMode = clientMode;
    }

    /// <summary>The local player's cast cooldown, in singleplayer and on a client.</summary>
    internal CastPacing LocalPacing { get; } = new();

    /// <summary>
    /// The effect seam: fired once per accepted cast in singleplayer and on a server, never on a
    /// client. <see cref="World"/> installs <see cref="SpellEffects.Cast"/> where it is authoritative.
    /// </summary>
    internal Action<SpellCast>? Effect { get; set; }

    /// <summary>
    /// The cosmetic side: called with the cast and whether it fizzled, for every accepted cast. On a client
    /// it is called as a prediction, never fizzled. <see cref="World"/> installs
    /// <see cref="SpellFeedback.OnCast"/> only where frames are drawn, so a headless server has none.
    /// </summary>
    internal Action<SpellCast, bool>? Feedback { get; set; }

    /// <summary>
    /// Judges and runs a cast of <paramref name="slot"/> for <paramref name="player"/> (see
    /// <see cref="CastRules.TryCast"/>): <see cref="CastVerdict.NotPlaying"/> unless the world is
    /// playing; on <see cref="CastVerdict.Cast"/> outside client mode the mana is billed and the
    /// effect fires (or the fizzle backfires); the <see cref="Feedback"/> hook follows any accepted cast.
    /// </summary>
    internal CastVerdict CastFor(PlayerContext player, int slot, CastPacing pacing, double now, double slackSeconds)
    {
        if (!_world.IsPlaying) return CastVerdict.NotPlaying;
        var verdict = CastRules.TryCast(player, slot, pacing, now, slackSeconds, spend: !_clientMode, out var cast);
        if (verdict != CastVerdict.Cast) return verdict;
        if (_clientMode)
        {
            Feedback?.Invoke(cast, false);
            return verdict;
        }
        // A fizzle still counts as a cast (the mana is gone, the gesture plays) but fires no effect
        var fizzled = CastRisk.Fizzles(cast.Stats, FizzleRoll());
        if (fizzled) Backfire(cast);
        else Effect?.Invoke(cast);
        Feedback?.Invoke(cast, fizzled);
        return verdict;
    }

    /// <summary>The fizzle roll in 0..1 (a test seam).</summary>
    internal Func<float> FizzleRoll { get; set; } = static () => Random.Shared.NextSingle();

    // A fizzle hurts the caster as a magic blow; a server tells the client
    private void Backfire(SpellCast cast)
    {
        var health = cast.Caster.Health;
        if (health == null) return;
        var damage = CastRisk.BackfireDamage(cast.Stats, PlayerHealth.MaxHealth, cast.Caster.IsCreative);
        if (damage <= 0) return;
        health.TakeDamage(damage, DamageSource.Magic, SpellConfig.BackfireDeathMessage);
        var players = _world.ServerPlayers;
        for (var i = 0; i < players.Count; i++)
            if (ReferenceEquals(players[i].Context, cast.Caster)) players[i].StatusDirty = true;
    }
}
