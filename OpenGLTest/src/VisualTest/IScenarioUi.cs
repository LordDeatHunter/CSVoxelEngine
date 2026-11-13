using System.Numerics;

namespace VoxelEngine.VisualTest;

/// <summary>
/// What a visual scenario may do to the live UI, so the director drives panels the way input would without
/// holding the app's input components. Implemented by the app (<c>Program</c> builds it from its own
/// components); every method throws <see cref="System.InvalidOperationException"/> when the UI cannot do it.
/// </summary>
public interface IScenarioUi
{
    /// <summary>Opens or closes the [E] panel.</summary>
    void SetPanelOpen(bool open);

    /// <summary>Switches the [E] panel to a tab by name (<c>blocks</c>, <c>tools</c>, <c>misc</c>, <c>gear</c>, <c>crafting</c>).</summary>
    void SelectTab(string tab);

    /// <summary>Types into the [E] panel's search field.</summary>
    void TypeSearch(string text);

    /// <summary>Clicks a listed row of the [E] panel, 0-based.</summary>
    void PickRow(int row);

    /// <summary>Holds the pointer at a canvas point for hover (tooltips, hovered rows), or null to give it back
    /// to the mouse.</summary>
    void SetPointer(Vector2? canvasPoint);

    /// <summary>Opens or closes the spell builder; opening needs a staff in hand.</summary>
    void SetSpellBuilderOpen(bool open);

    /// <summary>Opens the chest at a cell (the [E] list opens beside it); throws when the cell is not a chest in reach.</summary>
    void OpenChest(int x, int y, int z);

    /// <summary>Closes the chest panel.</summary>
    void CloseChest();

    /// <summary>Right-clicks the held or aimed lantern, then opens its fuel panel.</summary>
    void OpenLantern();

    /// <summary>Clicks a row of the open chest's list, 0-based: it moves one unit (the whole slot when
    /// <paramref name="all"/>) into the inventory.</summary>
    void PickChestRow(int row, bool all);

    /// <summary>
    /// Drives the waystone screen: <c>open</c> right-clicks the waystone at <paramref name="cell"/> (discovering it),
    /// <c>confirm</c> and <c>rename</c> press the popup's buttons, <c>type</c> types <paramref name="text"/> into the
    /// focused field, <c>submit</c> saves the rename, <c>back</c> is Escape, <c>close</c> closes, and <c>teleport</c>
    /// clicks list row <paramref name="row"/> (0-based).
    /// </summary>
    void Waystone(string action, Vector3? cell, string? text, int row);

    /// <summary>
    /// Drives the anvil's recipe list: <c>open</c> opens it on the anvil at <paramref name="cell"/> (a smithing hammer
    /// in hand and a metal stack on the anvil, as a right-click needs), <c>pick</c> clicks recipe card
    /// <paramref name="card"/> (0-based) and <c>close</c> closes it.
    /// </summary>
    void Forge(string action, Vector3? cell, int card);

    /// <summary>
    /// Drives the chat input: <c>type</c> opens the chat if it is closed and types <paramref name="text"/>, <c>tab</c>
    /// and <c>backtab</c> press Tab and Shift+Tab (the command autocomplete), <c>send</c> presses Enter and
    /// <c>close</c> closes it.
    /// </summary>
    void Chat(string action, string? text);
}
