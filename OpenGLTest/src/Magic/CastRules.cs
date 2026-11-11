using VoxelEngine.Content.Spells;
using VoxelEngine.Items;
using VoxelEngine.Player;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Magic;

/// <summary>
/// The pure judgement of one cast, GL-free and shared by singleplayer, a client's prediction and a
/// server's replay. The rules run in a fixed order: a staff in hand, the slot it has selected, that slot
/// bound, its parts known, the recipe valid with a finite cost, the caster's pacing, then the mana. The
/// mana is spent last, so every refusal leaves the pool and the pacing as they were.
/// </summary>
internal static class CastRules
{
    /// <summary>
    /// Everything but the pacing and the mana: the held staff, the slot (in range and the selected one),
    /// its binding, its parts and the compiled recipe. <paramref name="parts"/> is empty on an early
    /// refusal.
    /// </summary>
    public static CastVerdict Prepare(Inventory inventory, int slot, out StaffItem? staff, out SpellPart[] parts,
        out SpellStats stats)
    {
        parts = [];
        stats = default;
        if (!HeldStaff.TryGet(inventory, out staff, out var stack)) return CastVerdict.NoStaff;

        var component = stack.Get<StaffComponent>();
        if (slot < 0 || slot >= staff.Slots || slot != component.Selected) return CastVerdict.WrongSlot;
        if (!component.IsBound(slot)) return CastVerdict.Unbound;
        if (!StaffBinding.TryResolve(component.PartsAt(slot), out parts, out _)) return CastVerdict.UnknownPart;

        stats = new SpellRecipe(parts).Compile(staff.AllowedTags);
        return !stats.IsValid || !float.IsFinite(stats.Cost) ? CastVerdict.InvalidRecipe : CastVerdict.Cast;
    }

    /// <summary>
    /// Judges a cast of <paramref name="slot"/> for <paramref name="player"/> at <paramref name="now"/>.
    /// With <paramref name="spend"/> the mana is billed through <see cref="PlayerMana.TrySpend"/> (free in
    /// creative); without it (a client's prediction) the pool is only checked. On <see cref="CastVerdict.Cast"/>
    /// the pacing is marked and <paramref name="cast"/> describes the cast; any other verdict changes nothing.
    /// </summary>
    public static CastVerdict TryCast(PlayerContext player, int slot, CastPacing pacing, double now,
        double slackSeconds, bool spend, out SpellCast cast)
    {
        cast = default;
        var verdict = Prepare(player.Inventory, slot, out var staff, out var parts, out var stats);
        if (verdict != CastVerdict.Cast) return verdict;
        if (!pacing.IsReady(now, slackSeconds)) return CastVerdict.Cooldown;
        if (player.Health is not { } health) return CastVerdict.NoMana;

        var paid = spend
            ? health.Mana.TrySpend(stats.Cost, player.IsCreative)
            : player.IsCreative || health.Mana.Current >= stats.Cost;
        if (!paid) return CastVerdict.NoMana;

        pacing.Mark(now);
        cast = new SpellCast(player, staff!, slot, parts, stats, player.Eye, player.Forward);
        return CastVerdict.Cast;
    }
}
