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
/// The anvil's recipe list input: a right-click with a smithing hammer on an anvil holding a metal stack
/// (<see cref="RightClickOutcome.Forge"/>) opens it, listing only the recipes that metal and that many of it can make; a
/// click on a card forges it (<see cref="Simulation.Smithing.SmithingStations.Forge"/>: the stack gives up the recipe's
/// metal and the result goes into the inventory), and the list stays up for the next while the stack lasts. The cursor
/// is free and the look frozen while it is up; it closes on CLOSE, E or Escape, the pause menu, the death screen, putting
/// the hammer away, leaving the anvil's reach or the stack running out. Connected to a server, a pick is predicted here
/// and sent as a <see cref="NetIntentKind.Forge"/> intent; the server's inventory snapshot and station broadcast are the
/// truth. Owned by <see cref="GameplayInput"/>; main thread only.
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

    /// <summary>Opens the list on the anvil at <paramref name="cell"/> (the right-click chain judged the hammer and the
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

    /// <summary>Every playing frame: closes the list once its anvil no longer holds a stack, the hammer left the hand or
    /// the player left the anvil's reach, else shows the current stack and the hover.</summary>
    internal void Update(World w)
    {
        if (!View.IsOpen) return;
        var (x, y, z) = View.Cell;
        var center = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
        // The reach is measured to the face the ray hits; the center may lie up to a block's half-diagonal further
        if (!w.IsPlaying || !Mining.IsHammer(w.Inventory.HeldItem)
            || Vector3.Distance(w.Camera.Position, center) > World.InteractionReach + 1f
            || w.Smithing.Anvils.StateAt(x, y, z) is not { MetalCount: > 0 } anvil)
        {
            Close();
            return;
        }
        View.Show(anvil.MetalId, anvil.MetalCount);
        View.Hover = inputManager.Mouse is { } mouse ? renderManager.Ui.ToCanvas(mouse.Position) : null;
    }

    /// <summary>Routes one left-click on the list: a card forges its recipe, CLOSE closes.</summary>
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

    // Forges the card's recipe: locally in singleplayer; connected, predicted here and sent for the server to judge
    private void Pick(World w, int card)
    {
        var recipe = View.Recipes[card];
        var (x, y, z) = View.Cell;
        var cell = new Vector3(x, y, z);
        if (!w.Smithing.Forge(cell, recipe.ResultId, w.Inventory))
        {
            w.Toast.Show($"No room for {Inventory.DisplayName(recipe.ResultId)}");
            return;
        }
        if (session.Client != null)
            w.RaiseIntent(WorldIntent.Forge(w.Camera.Position, w.Camera.LastAimDirection, cell, recipe.ResultId));
        Update(w);
    }

    private void Capture(World w, bool open)
    {
        w.Camera.SuppressLook = open;
        inputManager.Mouse?.Cursor.CursorMode = open ? CursorMode.Normal : CursorMode.Disabled;
    }
}
