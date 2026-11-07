using System;
using System.Numerics;
using Silk.NET.Input;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Networking.Client;
using VoxelEngine.Platform;
using VoxelEngine.Rendering;
using VoxelEngine.Simulation.Chests;
using VoxelEngine.UI.Chest;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.App;

/// <summary>
/// The chest panel's input: opening a chest from a right-click (the [E] inventory list opens beside it), the clicks and
/// wheel over the chest card, deposits from the inventory list and the close. In singleplayer a move is applied to the
/// live chest at once; connected, it is sent to the server (<see cref="ClientSession.SendChestMove"/>) and the panel shows
/// the contents the server answers. Owned by <see cref="InventoryPanelInput"/>; main thread only.
/// </summary>
internal sealed class ChestInput
{
    private readonly InputManager inputManager;
    private readonly RenderManager renderManager;
    private readonly InventoryView inventoryView;
    private readonly ClientSession session;
    private readonly InventoryPanelInput panel;

    internal ChestInput(InputManager inputManager, RenderManager renderManager, InventoryView inventoryView,
        ClientSession session, InventoryPanelInput panel)
    {
        this.inputManager = inputManager;
        this.renderManager = renderManager;
        this.inventoryView = inventoryView;
        this.session = session;
        this.panel = panel;
    }

    private static World? ActiveWorld => Program.ActiveWorld;

    private bool Connected => session.Client != null;

    /// <summary>Whether a chest panel is up.</summary>
    internal bool IsOpen => ActiveWorld?.ChestPanel.IsOpen == true;

    /// <summary>
    /// Opens the chest at <paramref name="cell"/> for the local player: the [E] list opens beside it and, in
    /// singleplayer, the contents appear at once (a ruin chest rolling its loot); connected, the panel waits for the
    /// server's answer.
    /// </summary>
    internal void Open(World world, Vector3 cell)
    {
        var (x, y, z) = ((int)cell.X, (int)cell.Y, (int)cell.Z);
        var chunks = world.GetChunkManager();
        if (!chunks.TryGetCell(x, y, z, out var id, out _) || BlockType.ById[id] is not { Chest: not null } type) return;

        ChestState? contents = null;
        if (!Connected)
        {
            contents = ChestAccess.TryOpen(world.Chests, chunks, world.Camera.Position, x, y, z, World.InteractionReach);
            if (contents == null) return;
        }
        panel.SetInventoryOpen(true);
        world.ChestPanel.Show((x, y, z), type.DisplayName, contents);
        if (Connected)
            session.SendChestOpen(x, y, z);
    }

    /// <summary>Closes the chest panel (the [E] list stays up) and tells the server when connected.</summary>
    internal void Close()
    {
        if (ActiveWorld is not { } world || !world.ChestPanel.IsOpen) return;
        world.ChestPanel.Close();
        if (inventoryView.Focus == InventoryFocus.ChestSearch)
            inventoryView.Focus = InventoryFocus.None;
        if (Connected)
            session.SendChestClose();
    }

    /// <summary>Once a frame: a chest that is gone, or out of the player's reach, closes (singleplayer judges it here; a
    /// server judges its own and answers with the closed answer).</summary>
    internal void Update(World world)
    {
        var view = world.ChestPanel;
        if (!view.IsOpen) return;
        var (x, y, z) = view.Cell;
        var outOfReach = !ChestAccess.InReach(world.Camera.Position, x, y, z, World.InteractionReach);
        if (Connected)
        {
            if (outOfReach) Close();
            return;
        }
        var gone = world.Chests.Peek(x, y, z) == null
            || !ChestManager.TryProfileAt(world.GetChunkManager(), x, y, z, out _);
        if (gone || outOfReach)
            Close();
    }

    /// <summary>
    /// A left click while the chest panel is up: the search field takes focus, a row of the chest moves its stack into the
    /// inventory (one unit, or the whole slot with shift held). True when the click was on the card and is spent.
    /// </summary>
    internal bool HandleClick(Vector2 windowPosition)
    {
        if (ActiveWorld is not { } world || !world.ChestPanel.IsOpen) return false;
        var screen = renderManager.Ui.ScreenSize;
        var point = renderManager.Ui.ToCanvas(windowPosition);
        var view = world.ChestPanel;

        if (ChestCardLayout.SearchBox(screen).Contains(point))
        {
            inventoryView.Focus = InventoryFocus.ChestSearch;
            return true;
        }
        if (!ChestCardLayout.Contains(point, screen)) return false;

        var rows = view.Rows();
        var offset = ChestCardLayout.ClampedOffset(view.ScrollOffset, rows.Count, screen);
        if (ChestCardLayout.RowAt(point, screen, offset, rows.Count) is { } row)
            Send(world, ChestMove.Withdraw(rows[row], ShiftHeld ? 0 : 1));
        return true;
    }

    /// <summary>The wheel over the chest card scrolls its list; true when the card took it.</summary>
    internal bool HandleScroll(Vector2 windowPosition, float wheelY)
    {
        if (ActiveWorld is not { } world || !world.ChestPanel.IsOpen) return false;
        var screen = renderManager.Ui.ScreenSize;
        if (!ChestCardLayout.Contains(renderManager.Ui.ToCanvas(windowPosition), screen)) return false;
        var view = world.ChestPanel;
        var rows = view.Rows();
        view.ScrollOffset = ChestCardLayout.ClampedOffset(
            view.ScrollOffset - wheelY * ChestCardLayout.RowHeight * ChestCardLayout.WheelStepRows, rows.Count, screen);
        return true;
    }

    /// <summary>Moves one unit (the whole slot when <paramref name="all"/>) out of a chest slot into the inventory;
    /// the scenario runner's click on a chest row.</summary>
    internal void Withdraw(World world, int slot, bool all) => Send(world, ChestMove.Withdraw(slot, all ? 0 : 1));

    /// <summary>Moves a stackable (one unit, or all with shift) from the inventory into the open chest.</summary>
    internal void DepositStack(byte id)
    {
        if (ActiveWorld is not { } world || !world.ChestPanel.IsOpen) return;
        Send(world, ChestMove.DepositStack(id, ShiftHeld ? 0 : 1));
    }

    /// <summary>Moves a tool or piece of gear from the inventory into the open chest.</summary>
    internal void DepositInstance(int instanceId)
    {
        if (ActiveWorld is not { } world || !world.ChestPanel.IsOpen) return;
        Send(world, ChestMove.DepositInstance(instanceId));
    }

    /// <summary>Types a character into the focused chest search field.</summary>
    internal void Type(char c)
    {
        if (ActiveWorld is not { } world) return;
        if (!world.ChestPanel.Search.Text.TryAppend(c)) return;
        world.ChestPanel.ScrollOffset = 0f;
    }

    /// <summary>Backspace in the chest search field.</summary>
    internal void Backspace()
    {
        if (ActiveWorld is { } world && world.ChestPanel.Search.Text.Backspace())
            world.ChestPanel.ScrollOffset = 0f;
    }

    private bool ShiftHeld =>
        inputManager.Keyboard is { } keyboard && (keyboard.IsKeyPressed(Key.ShiftLeft) || keyboard.IsKeyPressed(Key.ShiftRight));

    // Singleplayer applies the move to the live chest and inventory; connected, the server judges it
    private void Send(World world, ChestMove move)
    {
        if (Connected)
        {
            session.SendChestMove(move);
            return;
        }
        if (world.ChestPanel.Contents is { } chest)
            ChestAccess.Apply(world.Inventory, chest, move);
    }
}
