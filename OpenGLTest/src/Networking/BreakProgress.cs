using System;
using VoxelEngine.Interaction;
using VoxelEngine.Items;

namespace VoxelEngine.Networking;

/// <summary>What the server made of a <see cref="NetIntentKind.BreakComplete"/>.</summary>
public enum BreakVerdict
{
    /// <summary>The block breaks.</summary>
    Break,
    /// <summary>No mining was started (or the last one already ended).</summary>
    NotStarted,
    /// <summary>The completion names another cell, or the cell no longer holds the block mined.</summary>
    OtherTarget,
    /// <summary>The held item changed since mining started, which restarts the break.</summary>
    HeldChanged,
    /// <summary>The block's <c>requiresTool</c> gate refuses the held item: it takes no damage.</summary>
    ToolRefused,
    /// <summary>Not enough hits fit into the time since mining started.</summary>
    TooFast,
}

/// <summary>
/// One player's mining on the server, the authoritative twin of the client's hold-to-mine loop in
/// <see cref="VoxelEngine.Interaction.LeftClickAction.UpdateBlockBreaking"/>. <see cref="Start"/>
/// records the target, the held item and when mining began; <see cref="Complete"/> breaks the
/// block only when the hits the held item lands at its pace since then add up to the block's
/// hardness, exactly as the client counts them:
/// the first hit lands the moment mining starts and one more every
/// <see cref="Mining.HitGapSeconds"/>, each removing <see cref="Mining.BlockDamage"/>. A changed
/// target or held item restarts the break, a refused tool never completes it, and a creative
/// player breaks at once. Any verdict ends the current break. GL-free; main thread only.
/// </summary>
public sealed class BreakProgress
{
    // Hits past this many are treated as never: an item with (next to) no block damage
    private const int MaxHits = 1_000_000;

    private bool _active;
    private int _x;
    private int _y;
    private int _z;
    private byte _blockId;
    private byte _heldId;
    private int _heldInstance;
    private bool _allowed;
    private double _startedAt;
    private double _requiredSeconds;

    /// <summary>Whether a break is in progress.</summary>
    public bool IsActive => _active;

    /// <summary>
    /// Starts (or restarts) mining the cell (<paramref name="x"/>, <paramref name="y"/>,
    /// <paramref name="z"/>) holding <paramref name="blockId"/> with state <paramref name="state"/>,
    /// with <paramref name="held"/> in hand (identified by <paramref name="heldId"/> and, for a tool,
    /// its <paramref name="heldInstance"/>), at server time <paramref name="now"/>.
    /// </summary>
    public void Start(int x, int y, int z, byte blockId, byte state, Item? held, byte heldId, int heldInstance,
        double now)
    {
        _active = true;
        (_x, _y, _z) = (x, y, z);
        _blockId = blockId;
        _heldId = heldId;
        _heldInstance = heldInstance;
        _allowed = Mining.CanMine(blockId, held);
        _startedAt = now;
        _requiredSeconds = RequiredSeconds(Mining.HardnessOf(blockId, state), Mining.BlockDamage(held),
            Mining.HitGapSeconds(held));
    }

    /// <summary>
    /// Judges a completion of the cell holding <paramref name="blockId"/> now, with the held item
    /// now identified by <paramref name="heldId"/>/<paramref name="heldInstance"/>, at server time
    /// <paramref name="now"/>. <paramref name="slackSeconds"/> forgives the start arriving later
    /// than the completion relative to when the client sent them (network jitter).
    /// </summary>
    public BreakVerdict Complete(int x, int y, int z, byte blockId, byte heldId, int heldInstance, bool creative,
        double now, double slackSeconds)
    {
        var verdict = Judge(x, y, z, blockId, heldId, heldInstance, creative, now, slackSeconds);
        _active = false;
        return verdict;
    }

    /// <summary>Abandons any break in progress.</summary>
    public void Reset() => _active = false;

    private BreakVerdict Judge(int x, int y, int z, byte blockId, byte heldId, int heldInstance, bool creative,
        double now, double slackSeconds)
    {
        if (creative) return BreakVerdict.Break;
        if (!_active) return BreakVerdict.NotStarted;
        if (x != _x || y != _y || z != _z || blockId != _blockId) return BreakVerdict.OtherTarget;
        if (heldId != _heldId || heldInstance != _heldInstance) return BreakVerdict.HeldChanged;
        if (!_allowed) return BreakVerdict.ToolRefused;
        return now - _startedAt + slackSeconds >= _requiredSeconds ? BreakVerdict.Break : BreakVerdict.TooFast;
    }

    /// <summary>
    /// Seconds from the first hit to the hit that breaks a block of <paramref name="hardness"/> when
    /// each hit deals <paramref name="damage"/> every <paramref name="hitGapSeconds"/>: hits are
    /// summed in float exactly like the client's loop, so both agree on the count. 0 for a block of
    /// hardness 0 (it pops on first contact); infinity when no number of hits would ever do it.
    /// </summary>
    public static double RequiredSeconds(float hardness, float damage, float hitGapSeconds)
    {
        if (hardness <= 0f) return 0.0;
        if (damage <= 0f || hardness / damage > MaxHits) return double.PositiveInfinity;
        var dealt = 0f;
        var hits = 0;
        while (dealt < hardness)
        {
            dealt += damage;
            hits++;
        }
        return (hits - 1) * (double)Math.Max(0f, hitGapSeconds);
    }
}
