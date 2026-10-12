using System;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.UI.Hud;

/// <summary>
/// The selected item's name shown above the hotbar for a moment after a switch, then faded out.
/// GL-free: <see cref="World"/> feeds it the inventory's selection once a frame and the HUD draws
/// <see cref="Name"/> at <see cref="Alpha"/>. It watches the selection rather than hooking the
/// input, so a hotbar key, the wheel, a pick-block, an equip into the selected slot and a
/// multiplayer client's inventory snapshot (the server's <c>SelectSlot</c> answer) all restart it
/// the same way. Main thread only.
/// </summary>
public sealed class HotbarNameFade
{
    private static float Total => WorldGenerationConfig.HotbarNameHoldSeconds + WorldGenerationConfig.HotbarNameFadeSeconds;

    private bool _tracked;
    private int _slot;

    /// <summary>The item the selected slot showed when last observed; 0 for an empty slot.</summary>
    public byte ShownId { get; private set; }

    /// <summary>Seconds since the selection last changed, parked at the end of the fade.</summary>
    public float Elapsed { get; private set; } = Total;

    /// <summary>The name to draw: the selected item's display name (a tool's custom name when it
    /// has one), or empty for an empty slot.</summary>
    public string Name { get; private set; } = "";

    /// <summary>Opacity of the name: 1 right after a switch, fading to 0; always 0 for an empty slot.</summary>
    public float Alpha => ShownId == 0
        ? 0f
        : MathUtils.HoldThenFade(Elapsed, WorldGenerationConfig.HotbarNameHoldSeconds,
            WorldGenerationConfig.HotbarNameFadeSeconds);

    /// <summary>
    /// Observes the selected slot once a frame. A change of the slot, or of the item it shows,
    /// restarts the timer and resolves the new name; the same selection again only advances it.
    /// </summary>
    public void Update(Inventory inventory, float deltaTime)
    {
        if (!Observe(inventory.SelectedIndex, inventory.SelectedBlockId, deltaTime))
            return;
        Name = ShownId == 0 ? "" : NameOf(inventory, ShownId);
    }

    /// <summary>
    /// The rule behind <see cref="Update"/>: returns true when <paramref name="slot"/> or
    /// <paramref name="id"/> differs from the last observation, which restarts the timer at 0;
    /// otherwise the timer advances by <paramref name="deltaTime"/>. The very first observation
    /// only records the selection, so a world does not open with a name over the hotbar.
    /// </summary>
    public bool Observe(int slot, byte id, float deltaTime)
    {
        var changed = _tracked && (slot != _slot || id != ShownId);
        _tracked = true;
        _slot = slot;
        ShownId = id;
        Elapsed = changed ? 0f : MathF.Min(Total, Elapsed + MathF.Max(0f, deltaTime));
        return changed;
    }

    // Only resolved on a change, so the tool-instance lookup never runs per frame.
    private static string NameOf(Inventory inventory, byte id)
    {
        var instance = inventory.SelectedToolInstance;
        if (instance != 0 && inventory.GetToolStack(instance) is { } stack)
            return stack.ResolvedName;
        return Inventory.DisplayName(id);
    }
}
