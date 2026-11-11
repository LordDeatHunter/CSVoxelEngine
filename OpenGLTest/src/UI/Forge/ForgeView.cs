using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.Recipes;
using VoxelEngine.Simulation.Smithing;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.UI.Forge;

/// <summary>
/// The anvil's recipe list, GL-free: whether it is up, the anvil it was opened on, the metal stack that anvil holds and
/// the recipes offered for it (<see cref="AnvilManager.Offered"/>: only that metal's, and only those the stack covers),
/// and the pointer for the hover look. What a click does to the world is <c>ForgeInput</c>, what is drawn
/// <c>ForgeRenderer</c>. Owned by <c>ForgeInput</c>; main thread only.
/// </summary>
public sealed class ForgeView
{
    // Whether Show has run since Open, so an unchanged stack never rebuilds the cards
    private bool _shown;

    /// <summary>Whether the list is up (it owns the mouse while it is).</summary>
    public bool IsOpen { get; private set; }

    /// <summary>The anvil it was opened on.</summary>
    public (int X, int Y, int Z) Cell { get; private set; }

    /// <summary>The metal stacked on that anvil, 0 for none.</summary>
    public ushort MetalId { get; private set; }

    /// <summary>How many of <see cref="MetalId"/> are stacked.</summary>
    public int MetalCount { get; private set; }

    /// <summary>The recipes the stack can make, in the order the cards show them.</summary>
    public IReadOnlyList<ForgeRecipe> Recipes { get; private set; } = [];

    /// <summary>The pointer in canvas units, for the hover look; null when there is none.</summary>
    public Vector2? Hover { get; set; }

    /// <summary>The list's open and close easing, stepped by the UI pass.</summary>
    public PanelTransition Transition { get; } = new();

    /// <summary>Opens the list on the anvil at <paramref name="cell"/>; its stack arrives with <see cref="Show"/>.</summary>
    public void Open((int X, int Y, int Z) cell)
    {
        IsOpen = true;
        Cell = cell;
        MetalId = 0;
        MetalCount = 0;
        Recipes = [];
        _shown = false;
    }

    /// <summary>Shows the anvil's current stack; the cards are rebuilt only when it changed.</summary>
    public void Show(ushort metalId, int count)
    {
        if (_shown && metalId == MetalId && count == MetalCount) return;
        _shown = true;
        MetalId = metalId;
        MetalCount = count;
        Recipes = AnvilManager.Offered(metalId, count);
    }

    /// <summary>Puts the list down.</summary>
    public void Close()
    {
        IsOpen = false;
        Hover = null;
    }
}
