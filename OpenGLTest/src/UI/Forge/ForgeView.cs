using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.Recipes;
using VoxelEngine.Simulation.WorkStations;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.UI.Forge;

/// <summary>
/// A work station's recipe list (the anvil's forge list, the leather table's patterns), GL-free: whether it is up, the
/// station it was opened on, the stack that station holds and the recipes offered for it
/// (<see cref="WorkStationManager.Offered"/>: only that input's, and only those the stack covers),
/// and the pointer for the hover look. What a click does to the world is <c>ForgeInput</c>, what is drawn
/// <c>ForgeRenderer</c>. Owned by <c>ForgeInput</c>; main thread only.
/// </summary>
public sealed class ForgeView
{
    // Whether Show has run since Open, so an unchanged stack never rebuilds the cards
    private bool _shown;

    /// <summary>Whether the list is up (it owns the mouse while it is).</summary>
    public bool IsOpen { get; private set; }

    /// <summary>The station it was opened on.</summary>
    public (int X, int Y, int Z) Cell { get; private set; }

    /// <summary>The station's recipe folder, null until <see cref="Show"/>.</summary>
    public string? Station { get; private set; }

    /// <summary>The station block's display name, for the title.</summary>
    public string Title { get; private set; } = "";

    /// <summary>The input stacked on that station, 0 for none.</summary>
    public ushort InputId { get; private set; }

    /// <summary>How many of <see cref="InputId"/> are stacked.</summary>
    public int InputCount { get; private set; }

    /// <summary>The result of the recipe already picked on the station, 0 for none; its card is marked.</summary>
    public ushort SelectedResultId { get; private set; }

    /// <summary>The recipes the stack can make, in the order the cards show them.</summary>
    public IReadOnlyList<WorkRecipe> Recipes { get; private set; } = [];

    /// <summary>The pointer in canvas units, for the hover look; null when there is none.</summary>
    public Vector2? Hover { get; set; }

    /// <summary>The list's open and close easing, stepped by the UI pass.</summary>
    public PanelTransition Transition { get; } = new();

    /// <summary>Opens the list on the station at <paramref name="cell"/>; its stack arrives with <see cref="Show"/>.</summary>
    public void Open((int X, int Y, int Z) cell)
    {
        IsOpen = true;
        Cell = cell;
        InputId = 0;
        InputCount = 0;
        Station = null;
        Title = "";
        SelectedResultId = 0;
        Recipes = [];
        _shown = false;
    }

    /// <summary>Shows the station's current stack and pick; the cards are rebuilt only when the stack changed.</summary>
    public void Show(string? station, string title, ushort inputId, int count, ushort selectedResultId)
    {
        SelectedResultId = selectedResultId;
        Title = title;
        if (_shown && station == Station && inputId == InputId && count == InputCount) return;
        _shown = true;
        Station = station;
        InputId = inputId;
        InputCount = count;
        Recipes = WorkStationManager.Offered(station, inputId, count);
    }

    /// <summary>Puts the list down.</summary>
    public void Close()
    {
        IsOpen = false;
        Hover = null;
    }
}
