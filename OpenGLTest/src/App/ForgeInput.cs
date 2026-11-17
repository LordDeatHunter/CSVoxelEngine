using System.Numerics;
using Silk.NET.Input;
using VoxelEngine.Interaction;
using VoxelEngine.Networking;
using VoxelEngine.Networking.Client;
using VoxelEngine.Platform;
using VoxelEngine.Rendering;
using VoxelEngine.UI.Forge;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.App;

/// <summary>
/// A work station's recipe list input: a right-click with the station's tool (a hammer on an anvil, a knife on a
/// leather table) on a station holding a stack (<see cref="RightClickOutcome.Forge"/>) opens it, listing only the
/// recipes that input and that many of it can make; a
/// click on a card picks it (<see cref="Simulation.Smithing.SmithingStations.Select"/>, which costs nothing yet) and
/// closes the list, and the tool's left-button hits then work it. The cursor is free and the look frozen while it is
/// up; it closes on a pick, CLOSE, E or Escape, the pause menu, the death screen, putting the tool away, leaving the
/// station's reach or the stack running out. Connected to a server, a pick is predicted here and sent as a
/// <see cref="NetIntentKind.Forge"/> intent; the server's station broadcast is the truth. Owned by
/// <see cref="GameplayInput"/>; main thread only.
/// </summary>
internal sealed class ForgeInput
{
    private readonly InputManager inputManager;
    private readonly RenderManager renderManager;
    private readonly ClientSession session;

    internal ForgeInput(InputManager inputManager, RenderManager renderManager, ClientSession session)
    {
        this.inputManager = inputManager;
        this.renderManager = renderManager;
        this.session = session;
    }

    /// <summary>The list's state, which the UI pass draws.</summary>
    internal ForgeView View { get; } = new();

    /// <summary>Whether the list is up (it owns the mouse while it is).</summary>
    internal bool IsOpen => View.IsOpen;

    /// <summary>Opens the list on the station at <paramref name="cell"/> (the right-click chain judged the tool and the
    /// stack).</summary>
    internal void Open(World w, Vector3 cell)
    {
        if (!w.IsPlaying) return;
        View.Open(((int)cell.X, (int)cell.Y, (int)cell.Z));
        Capture(w, true);
        Update(w);
    }

    /// <summary>Closes the list; <paramref name="recapture"/> false leaves the cursor to whatever is taking over (the
    /// pause menu).</summary>
    internal void Close(bool recapture = true)
    {
        if (!View.IsOpen) return;
        View.Close();
        if (recapture && Program.ActiveWorld is { } w) Capture(w, false);
    }

    /// <summary>Every playing frame: closes the list once its station no longer holds a stack, the tool left the hand or
    /// the player left the station's reach, else shows the current stack and the hover.</summary>
    internal void Update(World w)
    {
        if (!View.IsOpen) return;
        var (x, y, z) = View.Cell;
        var center = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
        // The reach is measured to the face the ray hits; the center may lie up to a block's half-diagonal further
        if (!w.IsPlaying || w.Smithing.WorkStations.StateAt(x, y, z) is not { InputCount: > 0 } station
            || BlockType.ById[station.BlockId] is not { WorkStation: { } profile } block
            || !Mining.IsStationTool(w.Inventory.HeldItem, profile)
            || Vector3.Distance(w.Camera.Position, center) > World.InteractionReach + 1f)
        {
            Close();
            return;
        }
        View.Show(profile.Recipes, block.DisplayName, station.InputId, station.InputCount, station.SelectedResultId);
        View.Hover = inputManager.Mouse is { } mouse ? renderManager.Ui.ToCanvas(mouse.Position) : null;
    }

    /// <summary>Routes one left-click on the list: a card picks its recipe, CLOSE closes.</summary>
    internal void HandleClick(Vector2 windowPos)
    {
        if (Program.ActiveWorld is not { IsPlaying: true } w || !View.IsOpen) return;
        switch (ForgeLayout.HitAt(renderManager.Ui.ToCanvas(windowPos), renderManager.Ui.ScreenSize, View.Recipes.Count,
                    out var card))
        {
            case ForgeHitKind.Recipe:
                Pick(w, card);
                break;
            case ForgeHitKind.Close:
                Close();
                break;
        }
    }

    /// <summary>A click on card <paramref name="card"/> (0-based), the scenario seam; false when no such card is
    /// listed.</summary>
    internal bool Pick(int card)
    {
        if (Program.ActiveWorld is not { } w || card < 0 || card >= View.Recipes.Count) return false;
        Pick(w, card);
        return true;
    }

    // Picks the card's recipe for the tool to work: locally in singleplayer; connected, predicted here and sent for
    // the server to judge. The list closes so the hammering can start.
    private void Pick(World w, int card)
    {
        var recipe = View.Recipes[card];
        var (x, y, z) = View.Cell;
        var cell = new Vector3(x, y, z);
        if (!w.Smithing.Select(cell, recipe.ResultId)) return;
        if (session.Client != null)
            w.RaiseIntent(WorldIntent.Forge(w.Camera.Position, w.Camera.LastAimDirection, cell, recipe.ResultId));
        w.Toast.Show($"Work the {Inventory.DisplayName(recipe.ResultId)}: {recipe.Hits} hits");
        Close();
    }

    private void Capture(World w, bool open)
    {
        w.Camera.SuppressLook = open;
        inputManager.Mouse?.Cursor.CursorMode = open ? CursorMode.Normal : CursorMode.Disabled;
    }
}
