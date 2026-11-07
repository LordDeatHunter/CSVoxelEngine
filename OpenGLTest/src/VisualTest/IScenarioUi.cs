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

    /// <summary>Switches the [E] panel to a tab by name (<c>blocks</c>, <c>tools</c>, <c>misc</c>, <c>crafting</c>).</summary>
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
}
